namespace SwitchPresence;

/// <summary>
/// Shared between the console and windowless builds so the two can never run at once, and so
/// `switch-presence stop` can end a windowless one without Task Manager.
/// </summary>
public static class AppInstance
{
    private const string MutexName = @"Local\SwitchPresence";
    private const string StopEventName = @"Local\SwitchPresence.stop";

    /// <summary>False when another instance already holds it; dispose the mutex to release.</summary>
    public static bool TryAcquire(out Mutex mutex)
    {
        mutex = new Mutex(initiallyOwned: true, MutexName, out var acquired);
        if (!acquired)
            mutex.Dispose();

        return acquired;
    }

    public static EventWaitHandle CreateStopEvent()
    {
        return new(false, EventResetMode.ManualReset, StopEventName);
    }

    /// <summary>False means nothing was running to stop.</summary>
    public static bool SignalStop()
    {
        try
        {
            using var stop = EventWaitHandle.OpenExisting(StopEventName);
            stop.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }
}