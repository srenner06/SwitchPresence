namespace SwitchPresence.Logging;

/// <summary>
/// One line sink, set by whichever executable is running. Core never writes to the console
/// itself: the windowless build has none.
/// </summary>
public static class Log
{
    public static Action<string> Sink { get; set; } = _ => { };

    public static void Info(string message) => Sink($"{DateTime.Now:HH:mm:ss} {message}");
    public static void Warn(string message) => Info($"WARN  {message}");
    public static void Error(string message) => Info($"ERROR {message}");
}
