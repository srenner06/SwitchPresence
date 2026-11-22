using SwitchPresence.Storage;

namespace SwitchPresence;

/// <summary>
/// Where the nxapi-auth client id comes from. Both executables resolve it the same way, so
/// the rule lives here rather than in either of them.
/// </summary>
public static class NxapiAuth
{
    /// <summary>
    /// Identifies this app to nxapi-auth, not any one user, so every copy ships the same one
    /// and users register nothing. A public OAuth client holds no secret by definition - it
    /// is readable in any released binary - so there is nothing here to hide.
    /// A fork should replace it; config.json and NXAPI_AUTH_CLIENT_ID both override it.
    /// </summary>
    public const string BuiltInClientId = "I50wogeuwnvdFSKN2Hsc9w";

    /// <summary>config.json, then the environment, then whatever the build shipped with.</summary>
    public static string? Resolve(AppConfig config)
    {
        var candidates = new[]
        {
            config.NxapiAuthClientId,
            Environment.GetEnvironmentVariable("NXAPI_AUTH_CLIENT_ID"),
            BuiltInClientId,
        };

        return candidates.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
    }

    /// <summary>How to obtain one. Shown before asking the user to type it in.</summary>
    public static string RegistrationHelp =>
        """
        switch-presence needs an nxapi-auth client id to reach the service that signs and
        encrypts Nintendo's requests. It is free and takes a minute:

          1. Open https://nxapi-auth.fancy.org.uk/oauth/clients
          2. Register a client of type "Public"
          3. Give it the scopes: ca:gf  ca:er  ca:dr
          4. Copy the client id from the top of its page
        """;

    /// <summary>For contexts that cannot ask, such as the windowless build.</summary>
    public static string MissingMessage => $"""
        {RegistrationHelp}

        Then either set NXAPI_AUTH_CLIENT_ID, or put "nxapiAuthClientId": "<id>" in
        {AppPaths.ConfigFile}
        """;
}