using System.Text.Json;
using SwitchPresence.Discord;
using SwitchPresence.Json;
using SwitchPresence.Logging;
using SwitchPresence.Nintendo;
using SwitchPresence.Storage;

namespace SwitchPresence.Presence;

/// <summary>
/// Poll, map, publish. Runs until cancelled and clears the Discord activity on the way out.
/// </summary>
public sealed class PresenceLoop(HttpClient http, AppConfig config, string nxapiAuthClientId)
{
    /// <summary>Backoff schedule for network and service errors. Never retries in a tight loop.</summary>
    static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
    ];

    /// <summary>How long a stale activity may stay up before it stops being honest.</summary>
    static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    readonly NintendoAccountClient _accounts = new(http);
    readonly ZncaApiClient _znca = new(http, nxapiAuthClientId);

    public async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.FriendNsaId))
            throw new InvalidOperationException("No account chosen yet. Run `switch-presence login` first.");

        var sessionToken = SecretStore.Read(AppPaths.SessionFile)
            ?? throw new InvalidOperationException("Not signed in. Run `switch-presence login` first.");

        var coral = new CoralClient(http, _znca, _accounts);
        var zncaVersion = config.ZncaVersionOverride ?? await _znca.GetZncaVersionAsync(ct);
        Log.Info($"watching {config.FriendName ?? config.FriendNsaId} (NSO app {zncaVersion})");

        await using var discord = new DiscordIpcClient();
        var session = LoadCachedSession();

        string? publishedJson = null;
        string? currentGame = null;
        var gameStartedAt = DateTimeOffset.UtcNow;
        var lastSuccess = DateTimeOffset.UtcNow;
        var failures = 0;
        var waitingForDiscord = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Polling Nintendo while nothing can consume the result just wastes their
                // service and ours, so pause entirely when Discord is not running.
                if (!DiscordIsRunning())
                {
                    if (!waitingForDiscord)
                    {
                        Log.Info("Discord is not running - polling paused");
                        waitingForDiscord = true;
                        publishedJson = null;
                        await discord.DisconnectAsync();
                    }
                    await Task.Delay(config.PollInterval, ct);
                    continue;
                }

                if (waitingForDiscord)
                {
                    Log.Info("Discord is back - resuming");
                    waitingForDiscord = false;
                }

                try
                {
                    if (session is null || !session.IsValid)
                        session = await LoginAndCacheAsync(coral, sessionToken, zncaVersion, ct);

                    var friends = await coral.GetFriendsAsync(session, zncaVersion, ct);
                    var friend = friends.FirstOrDefault(f => f.NsaId == config.FriendNsaId);

                    if (friend is null)
                        Log.Warn($"{config.FriendNsaId} is not in the friend list any more");

                    // The elapsed timer must survive polls, so only a different game resets it.
                    var gameName = friend?.Presence.Game?.Name;
                    if (gameName != currentGame)
                    {
                        currentGame = gameName;
                        gameStartedAt = DateTimeOffset.UtcNow;
                    }

                    var mapped = PresenceMapper.Map(friend?.Presence, config,
                        gameStartedAt.ToUnixTimeMilliseconds());

                    publishedJson = await PublishAsync(discord, mapped, publishedJson, ct);

                    lastSuccess = DateTimeOffset.UtcNow;
                    failures = 0;
                }
                catch (CoralException ex) when (ex.NeedsRelogin)
                {
                    Log.Info("Coral token rejected - signing in again");
                    session = null;
                    ClearCachedSession();
                    continue; // straight back round: this is a known state, not a failure
                }
                catch (Exception ex) when (ex is ApiException or CoralException or HttpRequestException)
                {
                    var delay = DelayAfterFailure(ex, failures++);

                    // A connection that has been down a while must stop claiming a live game.
                    if (publishedJson is not null && DateTimeOffset.UtcNow - lastSuccess > StaleAfter)
                    {
                        Log.Warn($"no data for {StaleAfter.TotalMinutes:0} minutes - clearing the activity");
                        await discord.TrySetActivityAsync(null, ct);
                        publishedJson = null;
                    }

                    await Task.Delay(delay, ct);
                    continue;
                }

                await Task.Delay(config.PollInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            if (discord.IsConnected)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await discord.TrySetActivityAsync(null, grace.Token); } catch { /* going away anyway */ }
            }
        }
    }

    /// <summary>Returns the JSON now live in Discord, or null if nothing is.</summary>
    async Task<string?> PublishAsync(DiscordIpcClient discord, MappedActivity? mapped, string? publishedJson,
        CancellationToken ct)
    {
        if (mapped is null)
        {
            if (publishedJson is null) return null;
            Log.Info("nothing playing - activity cleared");
            await discord.TrySetActivityAsync(null, ct);
            return null;
        }

        // Comparing the serialised form is the cheapest honest equality for these objects:
        // timestamps only move when the game changes, so identical JSON means nothing changed.
        var json = JsonSerializer.Serialize(mapped.Activity, AppJsonContext.Default.DiscordActivity);
        var reconnectNeeded = !discord.IsConnected || discord.ConnectedAppId != mapped.AppId;
        if (json == publishedJson && !reconnectNeeded) return publishedJson;

        if (reconnectNeeded && !await discord.TryConnectAsync(mapped.AppId, ct)) return null;

        if (!await discord.TrySetActivityAsync(mapped.Activity, ct)) return null;

        Log.Info($"{mapped.Activity.Details} ({mapped.Activity.State ?? "no status"})");
        return json;
    }

    static TimeSpan DelayAfterFailure(Exception ex, int previousFailures)
    {
        var delay = Backoff[Math.Min(previousFailures, Backoff.Length - 1)];

        // The API terms say Retry-After wins over any schedule of ours.
        if (ex is ApiException { RetryAfter: { } retryAfter } && retryAfter > delay) delay = retryAfter;

        if (ex is CoralException { Status: CoralStatus.UpgradeRequired })
            Log.Error("Nintendo no longer accepts the NSO app version we send. "
                + "An update to switch-presence is needed; retrying meanwhile.");
        else
            Log.Warn($"{ex.Message} - retrying in {Format(delay)}");

        return delay;
    }

    async Task<CoralSession> LoginAndCacheAsync(CoralClient coral, string sessionToken, string zncaVersion,
        CancellationToken ct)
    {
        var session = await coral.LoginAsync(sessionToken, zncaVersion, ct);
        SecretStore.Write(AppPaths.CoralTokenFile,
            $"{session.ExpiresAt.ToUnixTimeSeconds()}\n{session.AccessToken}");
        return session;
    }

    /// <summary>The terms require caching tokens for their full validity, restarts included.</summary>
    static CoralSession? LoadCachedSession()
    {
        if (SecretStore.Read(AppPaths.CoralTokenFile) is not { } cached) return null;

        var parts = cached.Split('\n', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var expiry)) return null;

        var session = new CoralSession(parts[1], DateTimeOffset.FromUnixTimeSeconds(expiry));
        return session.IsValid ? session : null;
    }

    static void ClearCachedSession() => SecretStore.Delete(AppPaths.CoralTokenFile);

    /// <summary>Cheap existence check, so we never handshake just to find out Discord is closed.</summary>
    static bool DiscordIsRunning()
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\", "discord-ipc-*").Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // cannot tell; assume yes rather than silently stopping
        }
    }

    static string Format(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} min" : $"{span.TotalSeconds:0} s";
}
