using SwitchPresence.Discord;
using SwitchPresence.Nintendo;
using SwitchPresence.Storage;

namespace SwitchPresence.Presence;

/// <summary>Which Discord application to appear as, and what to show.</summary>
public sealed record MappedActivity(string AppId, DiscordActivity Activity);

/// <summary>
/// Coral presence to Discord activity. Pure: no clock, no I/O, so the whole mapping is testable.
/// </summary>
public static class PresenceMapper
{
    private const int MaxDetailsLength = 128;

    /// <summary>null means "show nothing", which is a result, not a failure.</summary>
    public static MappedActivity? Map(CoralPresence? presence, AppConfig config, long startedAtUnixMs)
    {
        // OFFLINE is the console being off; INACTIVE is the HOME menu. Neither is a game.
        if (presence is null || presence.State is "OFFLINE" or "INACTIVE")
            return null;

        var game = presence.Game;
        if (string.IsNullOrWhiteSpace(game?.Name))
            return null;

        var isSwitch2 = presence.Platform == 2;
        var consoleName = isSwitch2 ? "Nintendo Switch 2" : "Nintendo Switch";

        return new MappedActivity(
            isSwitch2 ? config.DiscordClientIdSwitch2 : config.DiscordClientIdSwitch,
            new DiscordActivity
            {
                Type = 0,
                Details = Truncate(game.Name!, MaxDetailsLength),
                State = SecondLine(presence, game, config),
                StatusDisplayType = 2,
                Timestamps = new DiscordTimestamps { Start = startedAtUnixMs },
                Assets = string.IsNullOrWhiteSpace(game.ImageUri) ? null : new DiscordAssets
                {
                    LargeImage = game.ImageUri,
                    LargeText = $"Playing on {consoleName}",
                },
                Buttons = EshopButton(game, config),
            });
    }

    /// <summary>
    /// sysDescription is the game's own status text, but it is often an empty string rather
    /// than absent, so emptiness has to count as "not set".
    /// </summary>
    private static string? SecondLine(CoralPresence presence, PresenceGame game, AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(game.SysDescription))
            return game.SysDescription;

        return presence.State == "PLAYING" && config.ShowPlayingOnline ? "Playing online" : null;
    }

    private static DiscordButton[]? EshopButton(PresenceGame game, AppConfig config)
    {
        if (!config.ShowEshopButton)
            return null;

        if (game.ShopUri is not { } shop || !shop.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;

        return [new DiscordButton { Label = "Nintendo eShop", Url = shop }];
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }
}