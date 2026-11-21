using System.Text.Json.Serialization;

namespace SwitchPresence.Nintendo;

// ---- Nintendo Account ----

public sealed class SessionTokenResponse
{
    [JsonPropertyName("session_token")] public string SessionToken { get; set; } = "";
}

public sealed class NaTokenRequest
{
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = NintendoAccountClient.ClientId;
    [JsonPropertyName("session_token")] public string SessionToken { get; set; } = "";

    [JsonPropertyName("grant_type")]
    public string GrantType { get; set; } = "urn:ietf:params:oauth:grant-type:jwt-bearer-session-token";
}

public sealed class NaTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("id_token")] public string IdToken { get; set; } = "";
}

public sealed class NaUser
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "en-GB";
}

// ---- nxapi-auth / f-API ----

public sealed class ProtectedResourceMetadata
{
    [JsonPropertyName("authorization_servers")] public string[] AuthorizationServers { get; set; } = [];
}

public sealed class AuthServerMetadata
{
    [JsonPropertyName("token_endpoint")] public string TokenEndpoint { get; set; } = "";
}

public sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
}

public sealed class ZncaConfig
{
    [JsonPropertyName("nso_version")] public string NsoVersion { get; set; } = "";
}

public sealed class FRequest
{
    [JsonPropertyName("hash_method")] public string HashMethod { get; set; } = "1";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("na_id")] public string NaId { get; set; } = "";
    [JsonPropertyName("encrypt_token_request")] public EncryptTokenRequest? EncryptTokenRequest { get; set; }
}

public sealed class EncryptTokenRequest
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("parameter")] public LoginParameter Parameter { get; set; } = new();
}

public sealed class LoginParameter
{
    [JsonPropertyName("naIdToken")] public string NaIdToken { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "";
    [JsonPropertyName("timestamp")] public int Timestamp { get; set; }
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
    [JsonPropertyName("f")] public string F { get; set; } = "";
}

public sealed class FResponse
{
    [JsonPropertyName("encrypted_token_request")] public string EncryptedTokenRequest { get; set; } = "";
    [JsonPropertyName("warnings")] public System.Text.Json.JsonElement? Warnings { get; set; }
}

public sealed class EncryptRequestBody
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("data")] public string Data { get; set; } = "";
}

public sealed class DecryptRequestBody
{
    [JsonPropertyName("data")] public string Data { get; set; } = "";
}

// ---- Coral ----

public sealed class CoralLoginResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("result")] public CoralLoginResult? Result { get; set; }
}

public sealed class CoralLoginResult
{
    [JsonPropertyName("webApiServerCredential")] public WebApiServerCredential Credential { get; set; } = new();
}

public sealed class WebApiServerCredential
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("expiresIn")] public int ExpiresIn { get; set; }
}

public sealed class FriendListResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("result")] public FriendListResult? Result { get; set; }
}

public sealed class FriendListResult
{
    [JsonPropertyName("friends")] public Friend[] Friends { get; set; } = [];
}

public sealed class Friend
{
    [JsonPropertyName("nsaId")] public string NsaId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("presence")] public CoralPresence Presence { get; set; } = new();
}

public sealed class CoralPresence
{
    /// <summary>OFFLINE | INACTIVE | ONLINE | PLAYING. ONLINE means in a game; PLAYING means online in a game.</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "OFFLINE";

    /// <summary>1 = Switch, 2 = Switch 2. Absent on older records.</summary>
    [JsonPropertyName("platform")] public int? Platform { get; set; }

    /// <summary>An empty object when no game is running, so Name is the thing to test.</summary>
    [JsonPropertyName("game")] public PresenceGame? Game { get; set; }
}

public sealed class PresenceGame
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("imageUri")] public string? ImageUri { get; set; }
    [JsonPropertyName("shopUri")] public string? ShopUri { get; set; }
    [JsonPropertyName("sysDescription")] public string? SysDescription { get; set; }
}

/// <summary>Coral status codes we react to. 0 is success.</summary>
public static class CoralStatus
{
    public const int Ok = 0;
    public const int InvalidToken = 9403;
    public const int TokenExpired = 9404;
    public const int UpgradeRequired = 9427;
    public const int RateLimitExceeded = 9437;

    public static bool NeedsRelogin(int status) => status is InvalidToken or TokenExpired;

    public static string Describe(int status) => status switch
    {
        InvalidToken => "invalid token",
        TokenExpired => "token expired",
        UpgradeRequired => "the NSO app version we send is no longer accepted",
        RateLimitExceeded => "rate limited",
        _ => "unexpected Coral status",
    };
}
