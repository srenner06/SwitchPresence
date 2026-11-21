using SwitchPresence.Json;
using SwitchPresence.Logging;

namespace SwitchPresence.Nintendo;

/// <summary>A Coral session and when it stops being usable.</summary>
public sealed record CoralSession(string AccessToken, DateTimeOffset ExpiresAt)
{
    /// <summary>Five minutes of slack so a poll never starts with a token about to expire.</summary>
    public bool IsValid => DateTimeOffset.UtcNow < ExpiresAt - TimeSpan.FromMinutes(5);
}

/// <summary>
/// api-lp1.znc.srv.nintendo.net. Every request body is encrypted and every response decrypted
/// by the f-API, so each call here costs three HTTP round trips.
/// </summary>
public sealed class CoralClient(HttpClient http, ZncaApiClient znca, NintendoAccountClient accounts)
{
    public const string BaseUrl = "https://api-lp1.znc.srv.nintendo.net";

    public async Task<CoralSession> LoginAsync(string sessionToken, string zncaVersion, CancellationToken ct = default)
    {
        var tokens = await accounts.GetTokensAsync(sessionToken, ct);
        var user = await accounts.GetUserAsync(tokens.AccessToken, ct);
        var body = await znca.CreateLoginBodyAsync(tokens.IdToken, user.Id, user.Language, zncaVersion, ct);

        var json = await PostAsync("/v4/Account/Login", body, bearer: null, zncaVersion, ct);
        var response = ApiHttp.Parse(json, AppJsonContext.Default.CoralLoginResponse, "Account/Login");
        Require(response.Status, "Account/Login");

        var credential = response.Result!.Credential;
        Log.Info($"Coral session acquired, valid {credential.ExpiresIn / 60} min");
        return new CoralSession(credential.AccessToken, DateTimeOffset.UtcNow.AddSeconds(credential.ExpiresIn));
    }

    /// <summary>The friend list, which is the only place our own presence is visible.</summary>
    public async Task<Friend[]> GetFriendsAsync(CoralSession session, string zncaVersion,
        CancellationToken ct = default)
    {
        const string plain = """{"parameter":{}}""";
        var url = $"{BaseUrl}/v4/Friend/List";
        var body = await znca.EncryptRequestAsync(url, session.AccessToken, plain, zncaVersion, ct);

        var json = await PostAsync("/v4/Friend/List", body, session.AccessToken, zncaVersion, ct);
        var response = ApiHttp.Parse(json, AppJsonContext.Default.FriendListResponse, "Friend/List");
        Require(response.Status, "Friend/List");

        return response.Result?.Friends ?? [];
    }

    /// <summary>Posts the already-encrypted body and returns the decrypted JSON text.</summary>
    private async Task<string> PostAsync(string path, byte[] encryptedBody, string? bearer, string zncaVersion,
        CancellationToken ct)
    {
        var content = new ByteArrayContent(encryptedBody);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Platform", "Android");
        request.Headers.TryAddWithoutValidation("X-ProductVersion", zncaVersion);
        request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream,application/json");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB");
        request.Headers.TryAddWithoutValidation("User-Agent", $"com.nintendo.znca/{zncaVersion}(Android/12)");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        if (bearer is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");

        var (status, body, _, retryAfter) = await ApiHttp.RawAsync(http, request, ct);

        // Coral reports its own errors inside an encrypted body, so decrypt before judging the
        // HTTP status. Only give up if decryption itself fails.
        try
        {
            return await znca.DecryptResponseAsync(body, zncaVersion, ct);
        }
        catch (ApiException) when ((int)status is < 200 or > 299)
        {
            throw new ApiException($"Coral {path} HTTP {(int)status}: {ApiHttp.Text(body)}", status, retryAfter);
        }
    }

    private static void Require(int status, string what)
    {
        if (status == CoralStatus.Ok)
            return;

        throw new CoralException(status, $"{what} failed with status {status} ({CoralStatus.Describe(status)})");
    }
}

public sealed class CoralException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
    public bool NeedsRelogin => CoralStatus.NeedsRelogin(Status);
}