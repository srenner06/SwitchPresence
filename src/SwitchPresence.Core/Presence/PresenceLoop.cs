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
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
    ];

    /// <summary>How long a stale activity may stay up before it stops being honest.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly NintendoAccountClient _accounts = new(http);
    private readonly ZncaApiClient _znca = new(http, nxapiAuthClientId);
    private readonly DiscordIpcClient _discord = new();

    private CoralClient _coral = null!;
    private string _sessionToken = "";
    private string _zncaVersion = "";

    /// <summary>
    /// Re-read every tick rather than held, so changing the watched account - from the tray, or
    /// from `switch-presence login` in another process - takes effect without a restart.
    /// </summary>
    private AppConfig _config = config;

    /// <summary>
    /// The last friend list fetched. Coral returns all of them on every poll and we use one, so
    /// the account picker can reuse this instead of making its own call.
    /// </summary>
    public IReadOnlyList<Friend> LastFriends { get; private set; } = [];

    /// <summary>What to show in a tray tooltip. Null means nothing is playing.</summary>
    public Action<string?>? StatusChanged { get; set; }

    private CoralSession? _session;
    private string? _publishedJson;
    private string? _currentGame;
    private DateTimeOffset _gameStartedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastSuccess = DateTimeOffset.UtcNow;
    private int _failures;
    private bool _paused;

    public async Task RunAsync(CancellationToken ct)
    {
        await StartAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var wait = await TickAsync(ct);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            await ClearOnExitAsync();
        }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.FriendNsaId))
            throw new InvalidOperationException("No account chosen yet. Run `switch-presence login` first.");

        _sessionToken = SecretStore.Read(AppPaths.SessionFile)
            ?? throw new InvalidOperationException("Not signed in. Run `switch-presence login` first.");

        _coral = new CoralClient(http, _znca, _accounts);
        _zncaVersion = _config.ZncaVersionOverride ?? await _znca.GetZncaVersionAsync(ct);
        _session = LoadCachedSession();

        Log.Info($"watching {_config.FriendName ?? _config.FriendNsaId} (NSO app {_zncaVersion})");
    }

    /// <summary>One iteration. Returns how long to wait before the next one.</summary>
    private async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        _config = AppConfig.Load();

        // Polling Nintendo while nothing can consume the result wastes their service and
        // ours, so pause entirely while Discord is closed.
        if (!DiscordIsRunning())
        {
            await PauseAsync();
            return _config.PollInterval;
        }

        Resume();

        try
        {
            await EnsureSessionAsync(ct);

            var presence = await FetchPresenceAsync(ct);
            await PublishAsync(PresenceMapper.Map(presence, _config, StartTimestampFor(presence)), ct);

            _lastSuccess = DateTimeOffset.UtcNow;
            _failures = 0;
            return _config.PollInterval;
        }
        catch (CoralException ex) when (ex.NeedsRelogin)
        {
            // A known state rather than a failure, so no backoff and no failure count.
            Log.Info("Coral token rejected - signing in again");
            _session = null;
            SecretStore.Delete(AppPaths.CoralTokenFile);
            return TimeSpan.Zero;
        }
        catch (Exception ex) when (ex is ApiException or CoralException or HttpRequestException)
        {
            await ClearIfStaleAsync(ct);
            return DelayAfterFailure(ex, _failures++);
        }
    }

    private async Task<CoralPresence?> FetchPresenceAsync(CancellationToken ct)
    {
        var friends = await _coral.GetFriendsAsync(_session!, _zncaVersion, ct);
        LastFriends = friends;

        var friend = friends.FirstOrDefault(f => f.NsaId == _config.FriendNsaId);

        if (friend is null)
            Log.Warn($"{_config.FriendNsaId} is not in the friend list any more");

        return friend?.Presence;
    }

    /// <summary>The elapsed timer must survive polls, so only a different game resets it.</summary>
    private long StartTimestampFor(CoralPresence? presence)
    {
        var gameName = presence?.Game?.Name;
        if (gameName != _currentGame)
        {
            _currentGame = gameName;
            _gameStartedAt = DateTimeOffset.UtcNow;
        }

        return _gameStartedAt.ToUnixTimeMilliseconds();
    }

    private async Task PublishAsync(MappedActivity? mapped, CancellationToken ct)
    {
        if (mapped is null)
        {
            // Report every poll, not just the transition: with nothing playing at startup there
            // is no transition, and a listener would sit on its initial text forever.
            StatusChanged?.Invoke(null);

            if (_publishedJson is null)
                return;

            Log.Info("nothing playing - activity cleared");
            await _discord.TrySetActivityAsync(null, ct);
            _publishedJson = null;
            return;
        }

        // Comparing the serialised form is the cheapest honest equality for these objects:
        // timestamps only move when the game changes, so identical JSON means nothing changed.
        var json = JsonSerializer.Serialize(mapped.Activity, AppJsonContext.Default.DiscordActivity);
        var reconnectNeeded = !_discord.IsConnected || _discord.ConnectedAppId != mapped.AppId;

        if (json == _publishedJson && !reconnectNeeded)
            return;

        if (reconnectNeeded && !await _discord.TryConnectAsync(mapped.AppId, ct))
        {
            _publishedJson = null;
            return;
        }

        if (!await _discord.TrySetActivityAsync(mapped.Activity, ct))
        {
            _publishedJson = null;
            return;
        }

        _publishedJson = json;
        Log.Info($"{mapped.Activity.Details} ({mapped.Activity.State ?? "no status"})");
        StatusChanged?.Invoke(mapped.Activity.Details);
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is { IsValid: true })
            return;

        _session = await _coral.LoginAsync(_sessionToken, _zncaVersion, ct);
        SecretStore.Write(AppPaths.CoralTokenFile,
            $"{_session.ExpiresAt.ToUnixTimeSeconds()}\n{_session.AccessToken}");
    }

    private async Task PauseAsync()
    {
        if (_paused)
            return;

        Log.Info("Discord is not running - polling paused");
        StatusChanged?.Invoke("Waiting for Discord");
        _paused = true;
        _publishedJson = null;
        await _discord.DisconnectAsync();
    }

    private void Resume()
    {
        if (!_paused)
            return;

        Log.Info("Discord is back - resuming");
        _paused = false;
    }

    /// <summary>A connection that has been down a while must stop claiming a live game.</summary>
    private async Task ClearIfStaleAsync(CancellationToken ct)
    {
        if (_publishedJson is null || DateTimeOffset.UtcNow - _lastSuccess <= StaleAfter)
            return;

        Log.Warn($"no data for {StaleAfter.TotalMinutes:0} minutes - clearing the activity");
        await _discord.TrySetActivityAsync(null, ct);
        _publishedJson = null;
    }

    private async Task ClearOnExitAsync()
    {
        if (_discord.IsConnected)
        {
            // The caller's token is already cancelled, so shutdown needs a token of its own.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _discord.TrySetActivityAsync(null, grace.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // Going away anyway.
            }
        }

        await _discord.DisposeAsync();
    }

    private static TimeSpan DelayAfterFailure(Exception ex, int previousFailures)
    {
        var delay = Backoff[Math.Min(previousFailures, Backoff.Length - 1)];

        // The API terms say Retry-After wins over any schedule of ours.
        if (ex is ApiException { RetryAfter: { } retryAfter } && retryAfter > delay)
            delay = retryAfter;

        if (ex is CoralException { Status: CoralStatus.UpgradeRequired })
        {
            Log.Error("Nintendo no longer accepts the NSO app version we send. "
                + "An update to switch-presence is needed; retrying meanwhile.");
        }
        else
            Log.Warn($"{ex.Message} - retrying in {Humanize(delay)}");

        return delay;
    }

    /// <summary>The terms require caching tokens for their full validity, restarts included.</summary>
    private static CoralSession? LoadCachedSession()
    {
        if (SecretStore.Read(AppPaths.CoralTokenFile) is not { } cached)
            return null;

        var parts = cached.Split('\n', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var expiry))
            return null;

        var session = new CoralSession(parts[1], DateTimeOffset.FromUnixTimeSeconds(expiry));
        return session.IsValid ? session : null;
    }

    /// <summary>Cheap existence check, so we never handshake just to find out Discord is closed.</summary>
    private static bool DiscordIsRunning()
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

    private static string Humanize(TimeSpan span)
    {
        return span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} min" : $"{span.TotalSeconds:0} s";
    }
}
