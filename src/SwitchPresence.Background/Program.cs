using System.Diagnostics;
using SwitchPresence;
using SwitchPresence.Background;
using SwitchPresence.Logging;
using SwitchPresence.Presence;
using SwitchPresence.Storage;

// No CLI and no prompts: this build has no console. What it does have is a tray icon, which is
// how it reports that it is running and how it is quit. Everything interactive lives in
// switch-presence.exe, which this one launches when setup is needed.

internal class Program
{
    private static int Main(string[] args)
    {
        if (!AppInstance.TryAcquire(out var mutex))
            return 1;

        using (mutex)
        {
            var sink = new FileLogSink(Path.Combine(AppPaths.LogDir, "switch-presence.log"));
            Log.Sink = sink.Write;
            Log.Info($"switch-presence-bg started (pid {Environment.ProcessId})");

            var config = AppConfig.Load();

            if (NxapiAuth.Resolve(config) is not { } clientId)
                return Quit("No nxapi-auth client id is configured.");

            if (string.IsNullOrWhiteSpace(config.FriendNsaId) || SecretStore.Read(AppPaths.SessionFile) is null)
                return Quit("switch-presence is not set up yet.");

            using var cts = new CancellationTokenSource();
            using var stopEvent = AppInstance.CreateStopEvent();
            ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => cts.Cancel(), null, -1, executeOnlyOnce: true);

            // Sign-out and shutdown: the loop gets its chance to clear the Discord activity.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

            using var http = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };

            using var tray = new TrayIcon("switch-presence");
            var loop = new PresenceLoop(http, config, clientId) { StatusChanged = tray.UpdateStatus };

            tray.QuitRequested += () => cts.Cancel();
            tray.SetupRequested += () => StartConsole("login");
            tray.ChangeAccountRequested += () => StartConsole("account");

            // The pump owns this thread, so the loop runs beside it and ends it when it stops.
            var work = Task.Run(async () =>
            {
                try
                {
                    await loop.RunAsync(cts.Token);
                    Log.Info("stopped");
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }
                finally
                {
                    tray.Stop();
                }
            }, CancellationToken.None);

            tray.Run();

            cts.Cancel();
            work.Wait(TimeSpan.FromSeconds(5));
            return 0;
        }

        static int Quit(string reason)
        {
            Log.Error(reason);
            SetupPrompt.Show(reason);
            return 1;
        }
    }

    /// <summary>Hands an interactive job to the console build, which can actually ask things.</summary>
    private static void StartConsole(string command)
    {
        var console = Path.Combine(AppContext.BaseDirectory, "switch-presence.exe");

        if (!File.Exists(console))
        {
            Log.Error($"cannot find {console}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(console, command) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"could not start {console} {command}: {ex.Message}");
        }
    }
}
