using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SwitchPresence.Logging;

namespace SwitchPresence.Background;

/// <summary>
/// This build has no console, so an unconfigured start would otherwise write to a log nobody
/// is looking at and vanish. Say it where the user will see it, and offer to fix it.
/// </summary>
internal static class SetupPrompt
{
    private const uint MbOk = 0x0;
    private const uint MbYesNo = 0x4;
    private const uint MbIconInformation = 0x40;
    private const int IdYes = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);

    public static void Show(string problem)
    {
        var console = Path.Combine(AppContext.BaseDirectory, "switch-presence.exe");

        if (!File.Exists(console))
        {
            Ask($"{problem}\n\nRun switch-presence.exe to finish setting it up.", MbOk);
            return;
        }

        if (Ask($"{problem}\n\nOpen setup now?", MbYesNo) != IdYes)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(console, "login") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            Log.Error($"could not start {console}: {ex.Message}");
        }
    }

    private static int Ask(string text, uint buttons)
    {
        try
        {
            return MessageBoxW(0, text, "switch-presence", buttons | MbIconInformation);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error($"could not show a message box: {ex.Message}");
            return 0;
        }
    }
}
