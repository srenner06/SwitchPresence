using System.Runtime.InteropServices;

namespace SwitchPresence.Cli;

/// <summary>
/// Double-clicking an exe gives it a console window of its own, which Windows destroys the
/// instant the process exits - so any error message is unreadable. Detect that case and wait.
/// </summary>
internal static class ConsoleWindow
{
    // Blittable signature, so this needs no marshalling and stays NativeAOT-safe.
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    /// <summary>
    /// True when this process is the only one attached to the console, which means the window
    /// belongs to us rather than to a shell we were launched from.
    /// </summary>
    private static bool OwnsWindow()
    {
        try
        {
            return GetConsoleProcessList(new uint[2], 2) <= 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static void WaitBeforeClosing()
    {
        if (!OwnsWindow())
            return;

        Console.WriteLine();
        Console.Write("Press any key to close...");

        try
        {
            Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // No console input available; nothing to wait for.
        }
    }
}
