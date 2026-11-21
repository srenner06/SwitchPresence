using System.Text;
using System.Text.Json;
using SwitchPresence.Json;

namespace SwitchPresence.Nintendo;

/// <summary>accounts.nintendo.com: session token, short-lived tokens, and the account's own details.</summary>
public sealed class NintendoAccountClient(HttpClient http)
{
    internal const string ClientId = "71b963c1b7b6d119";
    internal const string RedirectUri = $"npf{ClientId}://auth";
    internal const string Scope = "openid user user.birthday user.screenName";

    /// <summary>Exchanges the code from the npf:// link for the long-lived session token.</summary>
    public async Task<string> GetSessionTokenAsync(string code, string verifier, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://accounts.nintendo.com/connect/1.0.0/api/session_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["session_token_code"] = code,
                ["session_token_code_verifier"] = verifier,
            }),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "NASDKAPI; Android");

        var response = await ApiHttp.JsonAsync(http, request, AppJsonContext.Default.SessionTokenResponse, ct);
        return response.SessionToken;
    }

    /// <summary>Short-lived (~15 min) tokens. Fetch fresh ones for every Coral login.</summary>
    public async Task<NaTokenResponse> GetTokensAsync(string sessionToken, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new NaTokenRequest { SessionToken = sessionToken },
            AppJsonContext.Default.NaTokenRequest);
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://accounts.nintendo.com/connect/1.0.0/api/token")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 8.0.0)");

        return await ApiHttp.JsonAsync(http, request, AppJsonContext.Default.NaTokenResponse, ct);
    }

    /// <summary>The account id and language the f-API and Coral login need.</summary>
    public async Task<NaUser> GetUserAsync(string accessToken, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.accounts.nintendo.com/2.0.0/users/me");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB");
        request.Headers.TryAddWithoutValidation("User-Agent", "NASDKAPI; Android");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        return await ApiHttp.JsonAsync(http, request, AppJsonContext.Default.NaUser, ct);
    }
}
