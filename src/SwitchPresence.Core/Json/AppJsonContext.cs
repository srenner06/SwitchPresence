using System.Text.Json.Serialization;
using SwitchPresence.Discord;
using SwitchPresence.Nintendo;
using SwitchPresence.Storage;

namespace SwitchPresence.Json;

/// <summary>
/// Every type that crosses a JSON boundary, registered for source generation. Reflection-based
/// serialisation does not survive NativeAOT trimming, so nothing may be serialised without
/// appearing here.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(SessionTokenResponse))]
[JsonSerializable(typeof(NaTokenRequest))]
[JsonSerializable(typeof(NaTokenResponse))]
[JsonSerializable(typeof(NaUser))]
[JsonSerializable(typeof(ProtectedResourceMetadata))]
[JsonSerializable(typeof(AuthServerMetadata))]
[JsonSerializable(typeof(OAuthTokenResponse))]
[JsonSerializable(typeof(ZncaConfig))]
[JsonSerializable(typeof(FRequest))]
[JsonSerializable(typeof(FResponse))]
[JsonSerializable(typeof(EncryptRequestBody))]
[JsonSerializable(typeof(DecryptRequestBody))]
[JsonSerializable(typeof(CoralLoginResponse))]
[JsonSerializable(typeof(FriendListResponse))]
[JsonSerializable(typeof(DiscordActivity))]
[JsonSerializable(typeof(DiscordHandshake))]
[JsonSerializable(typeof(SetActivityCommand))]
[JsonSerializable(typeof(DiscordFrame))]
public sealed partial class AppJsonContext : JsonSerializerContext;
