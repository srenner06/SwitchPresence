using System.Diagnostics;
using System.Text;
using SwitchPresence;
using SwitchPresence.Cli;
using SwitchPresence.Logging;
using SwitchPresence.Nintendo;
using SwitchPresence.Presence;
using SwitchPresence.Storage;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* some hosts refuse; game names may mangle */ }
        Log.Sink = Console.WriteLine;

        var exitCode = args.Length == 0 ? await RunAsync() : args[0].ToLowerInvariant() switch
        {
            "login" => await LoginAsync(),
            "autostart" => SetAutostart(args),
            "stop" => Stop(),
            "help" or "-h" or "--help" => Help(),
            var other => Fail($"unknown command '{other}'. Try `switch-presence help`."),
        };

        ConsoleWindow.WaitBeforeClosing();
        return exitCode;

        // ---------------------------------------------------------------- run

        async Task<int> RunAsync()
        {
            var config = AppConfig.Load();
            if (ResolveClientId(config) is not { } clientId)
                return 1;

            if (string.IsNullOrWhiteSpace(config.FriendNsaId))
                return Fail("Not set up yet. Run `switch-presence login` first.");

            if (!AppInstance.TryAcquire(out var mutex))
                return Fail("switch-presence is already running. Use `switch-presence stop` to end it.");

            using (mutex)
            {
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

                using var stopEvent = AppInstance.CreateStopEvent();
                ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => cts.Cancel(), null, -1, executeOnlyOnce: true);

                Console.WriteLine("Ctrl+C to stop.\n");
                using var http = NewHttpClient();
                try
                {
                    await new PresenceLoop(http, config, clientId).RunAsync(cts.Token);
                    Console.WriteLine("\nStopped; Discord activity cleared.");
                    return 0;
                }
                catch (Exception ex) when (ex is InvalidOperationException or LoginException)
                {
                    return Fail(ex.Message);
                }
            }
        }

        // ---------------------------------------------------------------- login

        async Task<int> LoginAsync()
        {
            var config = AppConfig.Load();
            if (ResolveClientId(config) is not { } clientId)
                return 1;

            Console.WriteLine(LoginFlow.Disclosure);
            Console.Write("\nType 'yes' to continue > ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
                return Fail("Not acknowledged. Nothing was sent anywhere.");

            var pending = LoginFlow.Start();
            Console.WriteLine($"\n{pending.AuthorizeUrl}\n");
            Console.WriteLine("Sign in with the SECOND account. On the 'Linking an External Account' page,");
            Console.WriteLine("right-click 'Select this person' and copy the link address - do not click it.");
            try { Process.Start(new ProcessStartInfo(pending.AuthorizeUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Console.WriteLine($"(could not open a browser: {ex.Message})"); }

            Console.Write("\nPaste the npf://... link > ");
            var pasted = Console.ReadLine() ?? "";

            using var http = NewHttpClient();
            var accounts = new NintendoAccountClient(http);

            try
            {
                var sessionToken = await pending.CompleteAsync(pasted, accounts);
                SecretStore.Write(AppPaths.SessionFile, sessionToken);
                Console.WriteLine("Signed in; the session token is encrypted with your Windows account.");

                var znca = new ZncaApiClient(http, clientId);
                var version = config.ZncaVersionOverride ?? await znca.GetZncaVersionAsync();
                var coral = new CoralClient(http, znca, accounts);

                Console.WriteLine("Fetching the friend list...");
                var session = await coral.LoginAsync(sessionToken, version);
                var friends = await coral.GetFriendsAsync(session, version);

                if (friends.Length == 0)
                    return Fail("That account has no friends yet. Add your main account as a friend, then try again.");

                var chosen = ChooseFriend(friends);
                config.FriendNsaId = chosen.NsaId;
                config.FriendName = chosen.Name;

                // Remember a client id that came from the environment, so running the exe
                // later - by double-click, or from autostart - needs no variable set.
                if (config.NxapiAuthClientId is null && clientId != NxapiAuth.BuiltInClientId)
                    config.NxapiAuthClientId = clientId;

                config.Save();

                Console.WriteLine($"\nWatching {chosen.Name}. Config: {AppPaths.ConfigFile}");
                Console.WriteLine("Run `switch-presence` to start.");
                return 0;
            }
            catch (Exception ex) when (ex is LoginException or ApiException or CoralException)
            {
                return Fail(ex.Message);
            }
        }

        Friend ChooseFriend(Friend[] friends)
        {
            Console.WriteLine("\nWhich account's games should be shown? (your MAIN account)\n");
            for (var i = 0; i < friends.Length; i++)
            {
                var presence = friends[i].Presence;
                var playing = presence.Game?.Name is { Length: > 0 } name ? $" - {name}" : "";
                Console.WriteLine($"  [{i + 1}] {friends[i].Name}  ({presence.State}{playing})");
            }

            while (true)
            {
                Console.Write($"\nNumber 1-{friends.Length} > ");
                if (int.TryParse(Console.ReadLine(), out var pick) && pick >= 1 && pick <= friends.Length)
                    return friends[pick - 1];

                Console.WriteLine("Not one of those.");
            }
        }

        // ---------------------------------------------------------------- autostart / stop

        int SetAutostart(string[] arguments)
        {
            var mode = arguments.Length > 1 ? arguments[1].ToLowerInvariant() : "";
            switch (mode)
            {
                case "on":
                    var target = Path.Combine(AppContext.BaseDirectory, "switch-presence-bg.exe");
                    if (!File.Exists(target))
                    {
                        if (Environment.ProcessPath is not { } fallback)
                            return Fail("cannot determine this executable's path");

                        target = fallback;
                        Console.WriteLine("switch-presence-bg.exe is not here, so autostart will open a console window.");
                    }

                    Autostart.Enable(target);
                    Console.WriteLine($"Autostart on: {target}");
                    return 0;

                case "off":
                    Autostart.Disable();
                    Console.WriteLine("Autostart off.");
                    return 0;

                default:
                    Console.WriteLine(Autostart.Current is { } current
                        ? $"Autostart is on: {current}"
                        : "Autostart is off.");
                    Console.WriteLine("Use `switch-presence autostart on` or `... off`.");
                    return 0;
            }
        }

        int Stop()
        {
            if (AppInstance.SignalStop())
            {
                Console.WriteLine("Stop signal sent.");
                return 0;
            }

            Console.WriteLine("Nothing is running.");
            return 0;
        }

        // ---------------------------------------------------------------- shared

        string? ResolveClientId(AppConfig config)
        {
            if (NxapiAuth.Resolve(config) is { } clientId)
                return clientId;

            Fail(NxapiAuth.MissingMessage);
            return null;
        }

        static HttpClient NewHttpClient()
        {
            return new(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        }

        int Help()
        {
            Console.WriteLine("""
        switch-presence - shows your Switch game as your Discord activity

          switch-presence                 run in this console (Ctrl+C to stop)
          switch-presence login           sign in and pick the account to watch
          switch-presence autostart on    start the windowless build at sign-in
          switch-presence autostart off
          switch-presence stop            stop a running instance
        """);
            return 0;
        }

        static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return 1;
        }
    }
}