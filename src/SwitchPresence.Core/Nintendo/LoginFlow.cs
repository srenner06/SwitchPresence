using System.Security.Cryptography;
using System.Text;

namespace SwitchPresence.Nintendo;

/// <summary>
/// The one-time sign-in, as data in and data out. Core shows nothing and reads nothing: a
/// console prompts with ReadLine, a GUI uses a textbox, and neither needs an abstraction.
/// </summary>
public static class LoginFlow
{
    /// <summary>
    /// What the user must be told, and acknowledge, before anything is sent. Required by the
    /// nxapi-znca-api developer terms, not optional politeness.
    /// </summary>
    public const string Disclosure = """
        Your Nintendo Account id_token, your Coral (Nintendo Switch Online) token, and ALL data
        sent to and received from the Coral API will be sent to a third-party service
        (nxapi-znca-api), operated independently of this project.

        Sign in with a SECOND, disposable Nintendo Account. Never your main one. Your main
        account is the one whose presence gets shown, and it is never signed in to.
        """;

    public static PendingLogin Start()
    {
        return new();
    }
}

public sealed class PendingLogin
{
    private readonly string _state;
    private readonly string _verifier;

    internal PendingLogin()
    {
        _state = Base64Url(RandomNumberGenerator.GetBytes(36));
        _verifier = Base64Url(RandomNumberGenerator.GetBytes(32));

        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier)));
        AuthorizeUrl = "https://accounts.nintendo.com/connect/1.0.0/authorize?" + string.Join("&",
            new Dictionary<string, string>
            {
                ["state"] = _state,
                ["redirect_uri"] = NintendoAccountClient.RedirectUri,
                ["client_id"] = NintendoAccountClient.ClientId,
                ["scope"] = NintendoAccountClient.Scope,
                ["response_type"] = "session_token_code",
                ["session_token_code_challenge"] = challenge,
                ["session_token_code_challenge_method"] = "S256",
                ["theme"] = "login_form",
            }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    /// <summary>Open this, sign in, then copy the link address of "Select this person".</summary>
    public string AuthorizeUrl { get; }

    /// <summary>
    /// Exchanges the pasted npf://auth#... link for the long-lived session token.
    /// Throws <see cref="LoginException"/> with a message worth showing the user verbatim.
    /// </summary>
    public async Task<string> CompleteAsync(string pastedLink, NintendoAccountClient accounts,
        CancellationToken ct = default)
    {
        var fragment = ParseFragment(pastedLink);

        if (fragment.TryGetValue("error", out var error))
            throw new LoginException($"Nintendo returned '{error}': {fragment.GetValueOrDefault("error_description")}");

        if (fragment.GetValueOrDefault("state") != _state)
            throw new LoginException("That link is from a different sign-in attempt. Start again.");

        if (!fragment.TryGetValue("session_token_code", out var code))
            throw new LoginException("That link has no session_token_code in it.");

        return await accounts.GetSessionTokenAsync(code, _verifier, ct);
    }

    /// <summary>The values live in the fragment, so a browser would never send them anywhere.</summary>
    internal static Dictionary<string, string> ParseFragment(string link)
    {
        var hash = link.IndexOf('#');
        if (hash < 0)
        {
            throw new LoginException("That is not the right link: it has no '#'. "
                + "Copy the link address of the button rather than clicking it.");
        }

        return link[(hash + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : "");
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public sealed class LoginException(string message) : Exception(message);