using SwitchPresence;
using SwitchPresence.Logging;
using SwitchPresence.Presence;
using SwitchPresence.Storage;

// No CLI, no prompts, no setup: this build has no window to show any of it in.
// Everything interactive lives in switch-presence.exe.

internal class Program
{
    private static async Task<int> Main(string[] args)
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
                return Quit("no nxapi-auth client id configured");

            if (string.IsNullOrWhiteSpace(config.FriendNsaId) || SecretStore.Read(AppPaths.SessionFile) is null)
                return Quit("not set up yet - run `switch-presence login` first");

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

            try
            {
                await new PresenceLoop(http, config, clientId).RunAsync(cts.Token);
                Log.Info("stopped");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
                return 1;
            }
        }

        static int Quit(string reason)
        {
            Log.Error(reason);
            return 1;
        }
    }
}