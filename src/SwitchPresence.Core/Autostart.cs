using Microsoft.Win32;

namespace SwitchPresence;

/// <summary>
/// Per-user autostart through the HKCU Run key. No admin rights, no service, no scheduled task.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "switch-presence";

    public static bool IsEnabled => Current is not null;

    public static string? Current
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
    }

    /// <summary>Points the Run key at the windowless build beside this executable.</summary>
    public static void Enable(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException($@"cannot open HKCU\{RunKey}");
        key.SetValue(ValueName, $"\"{executablePath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }
}