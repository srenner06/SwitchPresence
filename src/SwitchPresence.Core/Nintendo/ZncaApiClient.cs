using System.Text;
using System.Text.Json;
using SwitchPresence.Json;
using SwitchPresence.Logging;

namespace SwitchPresence.Nintendo;

/// <summary>
/// nxapi-znca-api: generates the anti-abuse `f` token and encrypts/decrypts Coral bodies,
/// neither of which can be done without the real Nintendo Switch Online Android app.
/// Authenticated with an nxapi-auth public OAuth client (client credentials).
/// </summary>
public sealed class ZncaApiClient(HttpClient http, string clientId)
{
    public const string UserAgent = "switch-presence/0.1.0 (+https://github.com/srenner06/SwitchPresence)";

    private const string Origin = "https://nxapi-znca-api.fancy.org.uk";
    private const string Api = $"{Origin}/api/znca/";
    private const string Scope = "ca:gf ca:er ca:dr";

    /// <summary>Required by the API terms, and must never be derived at runtime.</summary>
    private const string ClientVersion = "d8fAZDPzwimzQ7c6";

    /// <summary>Used only if the version cannot be discovered; see <see cref="GetZncaVersionAsync"/>.</summary>
    public const string FallbackZncaVersion = "3.6.0";

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry = DateTimeOffset.MinValue;
    private string? _tokenEndpoint;

    /// <summary>
    /// The NSO app version the service currently runs workers for. Sending a version with no
    /// worker fails with HTTP 406 before Coral is ever reached, and Nintendo bumps this
    /// regularly, so it is discovered rather than pinned.
    /// </summary>
    public async Task<string> GetZncaVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}config");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            var config = await ApiHttp.JsonAsync(http, request, AppJsonContext.Default.ZncaConfig, ct);
            if (!string.IsNullOrWhiteSpace(config.NsoVersion))
                return config.NsoVersion;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Log.Warn($"could not read the supported NSO version ({ex.Message}); using {FallbackZncaVersion}");
        }

        return FallbackZncaVersion;
    }

    /// <summary>The encrypted Coral login body, ready to POST to Account/Login unchanged.</summary>
    public async Task<byte[]> CreateLoginBodyAsync(string idToken, string naId, string language,
        string zncaVersion, CancellationToken ct = default)
    {
        var body = new FRequest
        {
            Token = idToken,
            NaId = naId,
            EncryptTokenRequest = new EncryptTokenRequest
            {
                Url = $"{CoralClient.BaseUrl}/v4/Account/Login",
                Parameter = new LoginParameter { NaIdToken = idToken, Language = language },
            },
        };

        var response = await PostJsonAsync($"{Api}f",
            JsonSerializer.Serialize(body, AppJsonContext.Default.FRequest),
            AppJsonContext.Default.FResponse, zncaVersion, ct);

        WarnAbout(response.Warnings);
        return Convert.FromBase64String(response.EncryptedTokenRequest);
    }

    /// <summary>
    /// Encrypts a Coral request body. Returns the bytes to POST as-is: despite what nxapi's
    /// shape suggests, this endpoint answers with application/octet-stream, not a JSON envelope.
    /// </summary>
    public async Task<byte[]> EncryptRequestAsync(string url, string coralToken, string json,
        string zncaVersion, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new EncryptRequestBody { Url = url, Token = coralToken, Data = json },
            AppJsonContext.Default.EncryptRequestBody);

        var (status, bytes, contentType, retryAfter) = await ApiHttp.RawAsync(
            http, await BuildAsync($"{Api}encrypt-request", body, zncaVersion, ct), ct);

        if ((int)status is < 200 or > 299)
            throw new ApiException($"encrypt-request HTTP {(int)status}: {ApiHttp.Text(bytes)}", status, retryAfter);

        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            throw new ApiException($"encrypt-request returned JSON, expected octet-stream: {ApiHttp.Text(bytes)}");

        return bytes;
    }

    /// <summary>Decrypts a Coral response into its plain JSON text.</summary>
    public async Task<string> DecryptResponseAsync(byte[] responseBody, string zncaVersion,
        CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new DecryptRequestBody { Data = Convert.ToBase64String(responseBody) },
            AppJsonContext.Default.DecryptRequestBody);

        var request = await BuildAsync($"{Api}decrypt-response", body, zncaVersion, ct);
        request.Headers.TryAddWithoutValidation("Accept", "text/plain");

        var (status, bytes, _, retryAfter) = await ApiHttp.RawAsync(http, request, ct);
        if ((int)status is < 200 or > 299)
            throw new ApiException($"decrypt-response HTTP {(int)status}: {ApiHttp.Text(bytes)}", status, retryAfter);

        return ApiHttp.Text(bytes);
    }

    private async Task<T> PostJsonAsync<T>(string url, string body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, string zncaVersion, CancellationToken ct)
    {
        try
        {
            return await ApiHttp.JsonAsync(http, await BuildAsync(url, body, zncaVersion, ct), typeInfo, ct);
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            // A rejected token is the one case the terms allow retrying: drop it and ask once more.
            _accessToken = null;
            return await ApiHttp.JsonAsync(http, await BuildAsync(url, body, zncaVersion, ct), typeInfo, ct);
        }
    }

    private async Task<HttpRequestMessage> BuildAsync(string url, string json, string zncaVersion, CancellationToken ct)
    {
        var bearer = await GetAccessTokenAsync(ct);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-znca-Client-Version", ClientVersion);
        request.Headers.TryAddWithoutValidation("X-znca-Platform", "Android");
        request.Headers.TryAddWithoutValidation("X-znca-Version", zncaVersion);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }

    /// <summary>Cached for its full validity period, as the API terms require.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiry)
            return _accessToken;

        _tokenEndpoint ??= await DiscoverTokenEndpointAsync(ct);

        var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["scope"] = Scope,
            }),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var token = await ApiHttp.JsonAsync(http, request, AppJsonContext.Default.OAuthTokenResponse, ct);
        _accessToken = token.AccessToken;
        _accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn) - 30);
        return _accessToken;
    }

    private async Task<string> DiscoverTokenEndpointAsync(CancellationToken ct)
    {
        var resourceRequest = new HttpRequestMessage(HttpMethod.Get, $"{Origin}/.well-known/oauth-protected-resource");
        resourceRequest.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        var resource = await ApiHttp.JsonAsync(http, resourceRequest,
            AppJsonContext.Default.ProtectedResourceMetadata, ct);

        if (resource.AuthorizationServers.Length == 0)
            throw new ApiException("the f-API named no authorization server");

        var issuer = resource.AuthorizationServers[0].TrimEnd('/');
        var serverRequest = new HttpRequestMessage(HttpMethod.Get, $"{issuer}/.well-known/oauth-authorization-server");
        serverRequest.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        var server = await ApiHttp.JsonAsync(http, serverRequest, AppJsonContext.Default.AuthServerMetadata, ct);
        return server.TokenEndpoint;
    }

    private static void WarnAbout(System.Text.Json.JsonElement? warnings)
    {
        if (warnings is { } w && w.ValueKind is not System.Text.Json.JsonValueKind.Null)
            Log.Warn($"f-API: {w}");
    }
}