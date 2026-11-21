using System.Text.Json;
using SwitchPresence.Json;

namespace SwitchPresence.Storage;

public static class AppPaths
{
    public static string DataDir { get; } = Ensure(Environment.SpecialFolder.ApplicationData);
    public static string LogDir { get; } =
        Ensure(Path.Combine(Ensure(Environment.SpecialFolder.LocalApplicationData), "logs"));

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string SessionFile => Path.Combine(DataDir, "session.bin");
    public static string CoralTokenFile => Path.Combine(DataDir, "coral.bin");

    private static string Ensure(Environment.SpecialFolder folder)
    {
        return Ensure(Path.Combine(Environment.GetFolderPath(folder), "switch-presence"));
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed class AppConfig
{
    /// <summary>nsaId of the main account, whose presence is shown. Set by `login`.</summary>
    public string? FriendNsaId { get; set; }
    public string? FriendName { get; set; }

    public int PollIntervalSeconds { get; set; } = 60;
    public bool ShowPlayingOnline { get; set; } = true;
    public bool ShowEshopButton { get; set; } = true;

    public string DiscordClientIdSwitch { get; set; } = "1573788454590873731";
    public string DiscordClientIdSwitch2 { get; set; } = "1573788516343873598";

    /// <summary>null uses the built-in project client id.</summary>
    public string? NxapiAuthClientId { get; set; }

    /// <summary>null discovers the version from the f-API at startup.</summary>
    public string? ZncaVersionOverride { get; set; }

    /// <summary>Poll interval clamped to the documented floor, whatever the file says.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(30, PollIntervalSeconds));

    public static AppConfig Load()
    {
        if (!File.Exists(AppPaths.ConfigFile))
            return new AppConfig();

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.ConfigFile),
                AppJsonContext.Default.AppConfig) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Logging.Log.Warn($"config.json unreadable ({ex.Message}); using defaults");
            return new AppConfig();
        }
    }

    public void Save()
    {
        File.WriteAllText(AppPaths.ConfigFile,
        JsonSerializer.Serialize(this, AppJsonContext.Default.AppConfig));
    }
}