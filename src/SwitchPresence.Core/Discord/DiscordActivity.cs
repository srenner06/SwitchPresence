using System.Text.Json.Serialization;

namespace SwitchPresence.Discord;

/// <summary>
/// The activity as Discord's local IPC expects it. Only the fields we actually set exist here;
/// nulls are omitted on serialisation.
/// </summary>
public sealed class DiscordActivity
{
    /// <summary>0 = Playing.</summary>
    [JsonPropertyName("type")] public int Type { get; set; }

    /// <summary>The game name. Discord truncates past 128 characters.</summary>
    [JsonPropertyName("details")] public string? Details { get; set; }

    [JsonPropertyName("state")] public string? State { get; set; }

    /// <summary>2 shows the game name rather than the app name in the member list.</summary>
    [JsonPropertyName("status_display_type")] public int? StatusDisplayType { get; set; }

    [JsonPropertyName("timestamps")] public DiscordTimestamps? Timestamps { get; set; }
    [JsonPropertyName("assets")] public DiscordAssets? Assets { get; set; }
    [JsonPropertyName("buttons")] public DiscordButton[]? Buttons { get; set; }
}

public sealed class DiscordTimestamps
{
    /// <summary>Unix milliseconds, not seconds.</summary>
    [JsonPropertyName("start")] public long Start { get; set; }
}

public sealed class DiscordAssets
{
    /// <summary>A plain https URL works; Discord proxies it through mp:external automatically.</summary>
    [JsonPropertyName("large_image")] public string? LargeImage { get; set; }
    [JsonPropertyName("large_text")] public string? LargeText { get; set; }
}

public sealed class DiscordButton
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

// ---- IPC frames ----

public sealed class DiscordHandshake
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
}

public sealed class SetActivityCommand
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "SET_ACTIVITY";
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("args")] public SetActivityArgs Args { get; set; } = new();
}

public sealed class SetActivityArgs
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("activity")] public DiscordActivity? Activity { get; set; }
}

public sealed class DiscordFrame
{
    [JsonPropertyName("evt")] public string? Evt { get; set; }
    [JsonPropertyName("cmd")] public string? Cmd { get; set; }
}
