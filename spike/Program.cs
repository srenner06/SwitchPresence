// Milestone 1 spike: handoff 5.2 -> 5.4, against live servers.
// Throwaway by design: no architecture, raw output everywhere.
// The nxapi-auth client id is NOT hardcoded - it stays out of git history.
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        const string NaClientId = "71b963c1b7b6d119";
        const string RedirectUri = "npf71b963c1b7b6d119://auth";
        const string NaScope = "openid user user.birthday user.screenName";

        // 3.6.0 as of 2026-11-21. The handoff's 3.5.0 was already stale: the f-API only runs
        // workers for current versions. Authoritative list: GET {FApiOrigin}/api/znca/config
        const string ZncaVersion = "3.6.0";
        const string ZncaClientVersion = "d8fAZDPzwimzQ7c6";      // must be static, per the API terms
        const string FApi = "https://nxapi-znca-api.fancy.org.uk/api/znca/";
        const string FApiOrigin = "https://nxapi-znca-api.fancy.org.uk";
        const string FApiScope = "ca:gf ca:er ca:dr";
        const string Coral = "https://api-lp1.znc.srv.nintendo.net";
        const string UserAgent = "switch-presence/0.1.0 (+https://github.com/srenner06/SwitchPresence)";
        const string CoralUserAgent = $"com.nintendo.znca/{ZncaVersion}(Android/12)";

        if (args is ["discord", ..])
            return await DiscordSpike.RunAsync(args);

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "switch-presence");
        Directory.CreateDirectory(dataDir);
        var sessionFile = Path.Combine(dataDir, "spike-session.txt");
        var coralFile = Path.Combine(dataDir, "spike-coral.txt");
        var clientIdFile = Path.Combine(dataDir, "nxapi-auth-client-id.txt");

        using var http = new HttpClient();

        // ---- nxapi-auth client id: env var, else a local file. Never committed. ----
        var nxapiClientId = Environment.GetEnvironmentVariable("NXAPI_AUTH_CLIENT_ID")?.Trim();
        if (string.IsNullOrEmpty(nxapiClientId) && File.Exists(clientIdFile))
            nxapiClientId = File.ReadAllText(clientIdFile).Trim();

        if (string.IsNullOrEmpty(nxapiClientId))
        {
            Console.Error.WriteLine("No nxapi-auth client id.");
            Console.Error.WriteLine($"Set NXAPI_AUTH_CLIENT_ID, or put it in:\n  {clientIdFile}");
            return 1;
        }

        // ---- Step 1: session token ----
        string sessionToken;
        if (File.Exists(sessionFile))
        {
            sessionToken = File.ReadAllText(sessionFile).Trim();
            Console.WriteLine($"[1] reusing session_token ({sessionFile})");
        }
        else
        {
            sessionToken = await LogInAsync();
            File.WriteAllText(sessionFile, sessionToken);
            Console.WriteLine($"[1] session_token saved to {sessionFile} (plaintext; DPAPI comes later)");
        }

        // ---- Step 2A/2B: Nintendo Account ----
        Console.WriteLine("\n[2A] POST connect/1.0.0/api/token");
        var naTokenReq = Json(HttpMethod.Post, "https://accounts.nintendo.com/connect/1.0.0/api/token", new
        {
            client_id = NaClientId,
            session_token = sessionToken,
            grant_type = "urn:ietf:params:oauth:grant-type:jwt-bearer-session-token",
        });
        naTokenReq.Headers.TryAddWithoutValidation("Accept", "application/json");
        naTokenReq.Headers.TryAddWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 8.0.0)");
        var naToken = await JsonAsync(naTokenReq);
        var naAccessToken = naToken.RootElement.GetProperty("access_token").GetString()!;
        var idToken = naToken.RootElement.GetProperty("id_token").GetString()!;
        Console.WriteLine($"     id_token {Preview(idToken)}");

        Console.WriteLine("\n[2B] GET 2.0.0/users/me");
        var meReq = new HttpRequestMessage(HttpMethod.Get, "https://api.accounts.nintendo.com/2.0.0/users/me");
        meReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {naAccessToken}");
        meReq.Headers.TryAddWithoutValidation("Accept-Language", "en-GB");
        meReq.Headers.TryAddWithoutValidation("User-Agent", "NASDKAPI; Android");
        meReq.Headers.TryAddWithoutValidation("Accept", "application/json");
        var me = await JsonAsync(meReq);
        var naId = me.RootElement.GetProperty("id").GetString()!;
        var language = me.RootElement.GetProperty("language").GetString()!;
        Console.WriteLine($"     na_id {naId}  language {language}");

        // ---- Step 3: nxapi-auth access token ----
        Console.WriteLine("\n[3] nxapi-auth discovery + client_credentials");
        var prm = await JsonAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{FApiOrigin}/.well-known/oauth-protected-resource"));
        var issuer = prm.RootElement.GetProperty("authorization_servers")[0].GetString()!;
        Console.WriteLine($"    issuer {issuer}");

        var asMeta = await JsonAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{issuer.TrimEnd('/')}/.well-known/oauth-authorization-server"));
        var tokenEndpoint = asMeta.RootElement.GetProperty("token_endpoint").GetString()!;
        Console.WriteLine($"    token_endpoint {tokenEndpoint}");

        var fTokenReq = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = nxapiClientId,
                ["scope"] = FApiScope,
            }),
        };
        fTokenReq.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        fTokenReq.Headers.TryAddWithoutValidation("Accept", "application/json");
        var fTokenRes = await JsonAsync(fTokenReq);
        var fToken = fTokenRes.RootElement.GetProperty("access_token").GetString()!;
        Console.WriteLine($"    granted scope: {Get(fTokenRes.RootElement, "scope") ?? "(none reported)"}");
        Console.WriteLine($"    access_token {Preview(fToken)}");

        // ---- Steps 4 + 5 ----
        var coralToken = LoadCoralToken();
        if (coralToken is null)
        {
            coralToken = await CoralLoginAsync();
            Console.WriteLine($"    coral token cached in {coralFile}");
        }
        else
            Console.WriteLine($"\n[4] reusing cached Coral token ({coralFile})");

        var friends = await FriendListAsync(coralToken);
        PrintFriends(friends);
        return 0;

        // ===================== steps =====================

        async Task<string> CoralLoginAsync()
        {
            Console.WriteLine("\n[4] POST /f  (hash_method 1 + encrypted login body)");
            var fReq = Json(HttpMethod.Post, $"{FApi}f", new
            {
                hash_method = "1",
                token = idToken,
                na_id = naId,
                encrypt_token_request = new
                {
                    url = $"{Coral}/v4/Account/Login",
                    parameter = new { naIdToken = idToken, language, timestamp = 0, requestId = "", f = "" },
                },
            });
            AddZncaHeaders(fReq, fToken);
            var f = await JsonAsync(fReq);
            if (f.RootElement.TryGetProperty("warnings", out var w))
                Console.WriteLine($"    WARNINGS from f-API: {w}");

            var encryptedLogin = Convert.FromBase64String(
                f.RootElement.GetProperty("encrypted_token_request").GetString()!);
            Console.WriteLine($"    encrypted login body: {encryptedLogin.Length} bytes");

            Console.WriteLine("\n[4b] POST /v4/Account/Login");
            var loginReq = Octet(HttpMethod.Post, $"{Coral}/v4/Account/Login", encryptedLogin);
            loginReq.Headers.TryAddWithoutValidation("X-Platform", "Android");
            loginReq.Headers.TryAddWithoutValidation("X-ProductVersion", ZncaVersion);
            loginReq.Headers.TryAddWithoutValidation("Accept", "application/octet-stream,application/json");
            loginReq.Headers.TryAddWithoutValidation("User-Agent", CoralUserAgent);
            var (loginStatus, loginBody, _) = await RawAsync(loginReq);
            Console.WriteLine($"     HTTP {(int)loginStatus}, {loginBody.Length} bytes");

            var login = await DecryptAsync(loginBody);
            var result = RequireCoralOk(login, "Account/Login");
            var cred = result.GetProperty("webApiServerCredential");
            var token = cred.GetProperty("accessToken").GetString()!;
            var expiresIn = cred.GetProperty("expiresIn").GetInt32();
            var user = result.GetProperty("user");
            Console.WriteLine($"     coral user: {Get(user, "name")}  nsaId {Get(user, "nsaId")}");
            Console.WriteLine($"     accessToken {Preview(token)}  expires in {expiresIn}s");

            File.WriteAllLines(coralFile,
                [DateTimeOffset.UtcNow.AddSeconds(expiresIn - 300).ToUnixTimeSeconds().ToString(), token]);
            return token;
        }

        async Task<JsonDocument> FriendListAsync(string token)
        {
            Console.WriteLine("\n[5] POST /encrypt-request  ->  /v4/Friend/List  ->  /decrypt-response");
            const string plain = "{\"parameter\":{}}";
            var encReq = Json(HttpMethod.Post, $"{FApi}encrypt-request",
                new { url = $"{Coral}/v4/Friend/List", token, data = plain });
            AddZncaHeaders(encReq, fToken);
            var (encStatus, encBody, encType) = await RawAsync(encReq);
            if ((int)encStatus is < 200 or > 299)
                throw new Exception($"{(int)encStatus} from encrypt-request\n{Text(encBody)}");

            // The handoff says this returns {"data": "<base64url>"}. It may instead return the
            // encrypted bytes directly - take whichever we actually got, and say which.
            byte[] encrypted;
            if (encType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var enc = JsonDocument.Parse(Text(encBody));
                if (enc.RootElement.TryGetProperty("warnings", out var w))
                    Console.WriteLine($"    WARNINGS from f-API: {w}");

                encrypted = FromBase64Url(enc.RootElement.GetProperty("data").GetString()!);
                Console.WriteLine($"    encrypt-request -> JSON envelope, {encrypted.Length} bytes decoded");
            }
            else
            {
                encrypted = encBody;
                Console.WriteLine($"    encrypt-request -> raw body ({encType ?? "no content-type"}), "
                    + $"{encrypted.Length} bytes used as-is; starts {Hex(encBody)}");
            }

            var listReq = Octet(HttpMethod.Post, $"{Coral}/v4/Friend/List", encrypted);
            listReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            listReq.Headers.TryAddWithoutValidation("Accept", "application/octet-stream,application/json");
            listReq.Headers.TryAddWithoutValidation("Accept-Language", "en-GB");
            listReq.Headers.TryAddWithoutValidation("User-Agent", CoralUserAgent);
            listReq.Headers.TryAddWithoutValidation("X-Platform", "Android");
            listReq.Headers.TryAddWithoutValidation("Pragma", "no-cache");
            listReq.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            var (status, body, _) = await RawAsync(listReq);
            Console.WriteLine($"    HTTP {(int)status}, {body.Length} bytes");

            var doc = await DecryptAsync(body);
            RequireCoralOk(doc, "Friend/List");
            return doc;
        }

        void PrintFriends(JsonDocument doc)
        {
            var friends = doc.RootElement.GetProperty("result").GetProperty("friends");
            Console.WriteLine($"\n=== {friends.GetArrayLength()} friends ===");
            foreach (var f in friends.EnumerateArray())
            {
                var p = f.GetProperty("presence");
                var state = Get(p, "state");
                var platform = p.TryGetProperty("platform", out var pl) && pl.ValueKind == JsonValueKind.Number
                    ? pl.GetInt32() switch { 1 => "Switch 1", 2 => "Switch 2", var n => $"platform {n}" }
                    : "platform ?";
                var hasGame = p.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.Object
                    && g.TryGetProperty("name", out _);
                var game = hasGame ? Get(g, "name") : null;

                Console.WriteLine($"  {Get(f, "name"),-20} {state,-9} {platform,-10} {game ?? "-"}");
                Console.WriteLine($"    nsaId {Get(f, "nsaId")}");
                if (hasGame)
                {
                    Console.WriteLine($"    imageUri: {Get(g, "imageUri")}");
                    Console.WriteLine($"    sysDescription: {Get(g, "sysDescription")}   shopUri: {Get(g, "shopUri")}");
                }
            }

            Console.WriteLine("\nThe main account's nsaId above is what goes in the config.");

            // Sanitise names/nsaIds before any of this becomes a committed test fixture.
            Console.WriteLine("\n=== raw Friend/List JSON (for test fixtures) ===");
            Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        }

        // ===================== plumbing =====================

        async Task<JsonDocument> DecryptAsync(byte[] body)
        {
            var req = Json(HttpMethod.Post, $"{FApi}decrypt-response", new { data = Convert.ToBase64String(body) });
            AddZncaHeaders(req, fToken);
            req.Headers.TryAddWithoutValidation("Accept", "text/plain");
            var (status, bytes, _) = await RawAsync(req);
            var text = Text(bytes);
            if (status != HttpStatusCode.OK)
                throw new Exception($"decrypt-response HTTP {(int)status}: {text}");

            return JsonDocument.Parse(text);
        }

        static JsonElement RequireCoralOk(JsonDocument doc, string what)
        {
            var status = doc.RootElement.GetProperty("status").GetInt32();
            if (status == 0)
                return doc.RootElement.GetProperty("result");

            var hint = status switch
            {
                9403 => "INVALID_TOKEN - re-login",
                9404 => "TOKEN_EXPIRED - re-login",
                9427 => "UPGRADE_REQUIRED - the hardcoded ZncaVersion is stale",
                9437 => "RATE_LIMIT_EXCEEDED - back off",
                _ => "see nxapi coral-types.ts",
            };
            throw new Exception($"{what} returned status {status} ({hint})\n{doc.RootElement}");
        }

        void AddZncaHeaders(HttpRequestMessage req, string bearer)
        {
            req.Headers.TryAddWithoutValidation("X-znca-Client-Version", ZncaClientVersion);
            req.Headers.TryAddWithoutValidation("X-znca-Platform", "Android");
            req.Headers.TryAddWithoutValidation("X-znca-Version", ZncaVersion);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        }

        async Task<(HttpStatusCode Status, byte[] Body, string? Type)> RawAsync(HttpRequestMessage req)
        {
            var res = await http.SendAsync(req);
            if (res.Headers.TryGetValues("Retry-After", out var ra))
                Console.WriteLine($"    (Retry-After: {string.Join(",", ra)})");

            return (res.StatusCode, await res.Content.ReadAsByteArrayAsync(), res.Content.Headers.ContentType?.MediaType);
        }

        async Task<JsonDocument> JsonAsync(HttpRequestMessage req)
        {
            var (status, bytes, type) = await RawAsync(req);
            var text = Text(bytes);
            if ((int)status is < 200 or > 299)
                throw new Exception($"{(int)status} from {req.RequestUri}\n{text}");

            try { return JsonDocument.Parse(text); }
            catch (JsonException ex)
            {
                throw new Exception($"Not JSON from {req.RequestUri}\n  Content-Type: {type ?? "(none)"}, {bytes.Length} bytes"
                    + $"\n  first bytes: {Hex(bytes)}\n  as text: {text[..Math.Min(300, text.Length)]}\n  {ex.Message}");
            }
        }

        async Task<string> LogInAsync()
        {
            Console.WriteLine("Before signing in, you must acknowledge this (required by the API terms):");
            Console.WriteLine();
            Console.WriteLine("  Your Nintendo Account id_token, your Coral (Nintendo Switch Online) token,");
            Console.WriteLine("  and ALL data sent to and received from the Coral API will be sent to a");
            Console.WriteLine("  third-party service (nxapi-znca-api), operated independently of this project.");
            Console.WriteLine();
            Console.WriteLine("  Sign in with a SECOND, disposable Nintendo Account. Never your main one.");
            Console.WriteLine();
            Console.Write("Type 'yes' to continue > ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Not acknowledged - nothing was sent anywhere.");

            var state = Base64Url(RandomNumberGenerator.GetBytes(36));
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

            var url = "https://accounts.nintendo.com/connect/1.0.0/authorize?" + string.Join("&",
                new Dictionary<string, string>
                {
                    ["state"] = state,
                    ["redirect_uri"] = RedirectUri,
                    ["client_id"] = NaClientId,
                    ["scope"] = NaScope,
                    ["response_type"] = "session_token_code",
                    ["session_token_code_challenge"] = challenge,
                    ["session_token_code_challenge_method"] = "S256",
                    ["theme"] = "login_form",
                }.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            Console.WriteLine($"\n{url}\n");
            Console.WriteLine("After signing in, on the 'Linking an External Account' page:");
            Console.WriteLine("right-click 'Select this person' -> Copy link address, then paste it here.");
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Console.WriteLine($"(couldn't open a browser: {ex.Message})"); }
            Console.Write("\nnpf...://auth#... > ");

            var pasted = (Console.ReadLine() ?? "").Trim();
            var hash = pasted.IndexOf('#');
            if (hash < 0)
                throw new Exception("No '#' in that link - copy the link address, don't follow it.");

            var frag = ParseQuery(pasted[(hash + 1)..]);
            if (frag.TryGetValue("error", out var err))
                throw new Exception($"Nintendo error '{err}': {frag.GetValueOrDefault("error_description")}");

            if (frag.GetValueOrDefault("state") != state)
                throw new Exception("state mismatch - that link is from a different login attempt.");

            if (!frag.TryGetValue("session_token_code", out var code))
                throw new Exception("no session_token_code in that link.");

            var req = new HttpRequestMessage(HttpMethod.Post,
                "https://accounts.nintendo.com/connect/1.0.0/api/session_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = NaClientId,
                    ["session_token_code"] = code,
                    ["session_token_code_verifier"] = verifier,
                }),
            };
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "NASDKAPI; Android");
            return (await JsonAsync(req)).RootElement.GetProperty("session_token").GetString()!;
        }

        string? LoadCoralToken()
        {
            if (!File.Exists(coralFile))
                return null;

            var lines = File.ReadAllLines(coralFile);
            if (lines.Length < 2 || !long.TryParse(lines[0], out var exp))
                return null;

            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < exp ? lines[1] : null;
        }

        static HttpRequestMessage Json(HttpMethod m, string url, object body)
        {
            return new(m, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
        }

        static HttpRequestMessage Octet(HttpMethod m, string url, byte[] body)
        {
            var c = new ByteArrayContent(body);
            c.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
            return new HttpRequestMessage(m, url) { Content = c };
        }

        static string? Get(JsonElement e, string name)
        {
            return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        static Dictionary<string, string> ParseQuery(string s)
        {
            return s.Split('&', StringSplitOptions.RemoveEmptyEntries)
             .Select(p => p.Split('=', 2))
             .ToDictionary(p => Uri.UnescapeDataString(p[0]),
                           p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
        }

        static string Base64Url(byte[] b)
        {
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static byte[] FromBase64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
        }

        static string Preview(string s)
        {
            return s.Length <= 24 ? s : $"{s[..10]}...{s[^4..]} ({s.Length} ch)";
        }

        // UTF-8 with any BOM stripped - System.Text.Json rejects a leading BOM.
        static string Text(byte[] b)
        {
            return Encoding.UTF8.GetString(b).TrimStart('\uFEFF');
        }

        static string Hex(byte[] b)
        {
            return Convert.ToHexString(b.AsSpan(0, Math.Min(24, b.Length)));
        }
    }
}