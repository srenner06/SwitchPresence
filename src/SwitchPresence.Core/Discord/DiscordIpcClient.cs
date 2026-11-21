using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SwitchPresence.Json;
using SwitchPresence.Logging;

namespace SwitchPresence.Discord;

/// <summary>
/// Discord's local IPC over a named pipe. No library: the protocol is an opcode, a length and
/// some JSON. Only the desktop client opens this pipe; the browser version has none.
/// </summary>
public sealed class DiscordIpcClient : IAsyncDisposable
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpPing = 3;
    private const int OpPong = 4;

    private NamedPipeClientStream? _pipe;

    /// <summary>Which app the current handshake used; changing it means reconnecting.</summary>
    public string? ConnectedAppId { get; private set; }

    public bool IsConnected => _pipe is { IsConnected: true };

    /// <summary>False simply means Discord is not running. That is normal, not an error.</summary>
    public async Task<bool> TryConnectAsync(string appId, CancellationToken ct = default)
    {
        await DisconnectAsync();

        for (var i = 0; i <= 9; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}",
                PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500, ct);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
                continue;
            }

            _pipe = pipe;
            try
            {
                await SendAsync(OpHandshake,
                    JsonSerializer.Serialize(new DiscordHandshake { ClientId = appId },
                        AppJsonContext.Default.DiscordHandshake), ct);

                var (op, json) = await ReceiveAsync(ct);
                var frame = JsonSerializer.Deserialize(json, AppJsonContext.Default.DiscordFrame);
                if (op != OpFrame || frame?.Evt != "READY")
                {
                    Log.Warn($"Discord refused the handshake for app {appId}: {json}");
                    await DisconnectAsync();
                    return false;
                }

                ConnectedAppId = appId;
                Log.Info($"connected to Discord (discord-ipc-{i})");
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                await DisconnectAsync();
                return false;
            }
        }

        return false;
    }

    /// <summary>null clears the activity. False means the pipe died and a reconnect is needed.</summary>
    public async Task<bool> TrySetActivityAsync(DiscordActivity? activity, CancellationToken ct = default)
    {
        if (!IsConnected)
            return false;

        try
        {
            var command = new SetActivityCommand
            {
                Nonce = Guid.NewGuid().ToString(),
                Args = new SetActivityArgs { Pid = Environment.ProcessId, Activity = activity },
            };
            await SendAsync(OpFrame,
                JsonSerializer.Serialize(command, AppJsonContext.Default.SetActivityCommand), ct);

            var (_, json) = await ReceiveAsync(ct);
            var frame = JsonSerializer.Deserialize(json, AppJsonContext.Default.DiscordFrame);
            if (frame?.Evt == "ERROR")
            {
                Log.Error($"Discord rejected the activity: {json}");
                return true; // the pipe is fine; the payload was not
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Warn($"Discord connection lost ({ex.Message})");
            await DisconnectAsync();
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_pipe is not null)
            await _pipe.DisposeAsync();

        _pipe = null;
        ConnectedAppId = null;
    }

    public ValueTask DisposeAsync()
    {
        return new(DisconnectAsync());
    }

    private async Task SendAsync(int opcode, string json, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), payload.Length);

        await _pipe!.WriteAsync(header, ct);
        await _pipe.WriteAsync(payload, ct);
        await _pipe.FlushAsync(ct);
    }

    /// <summary>Answers PINGs transparently so a quiet connection is not dropped.</summary>
    private async Task<(int Op, string Json)> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            var header = new byte[8];
            await _pipe!.ReadExactlyAsync(header, ct);
            var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));

            var payload = new byte[length];
            await _pipe.ReadExactlyAsync(payload, ct);
            var json = Encoding.UTF8.GetString(payload);

            if (opcode != OpPing)
                return (opcode, json);

            await SendAsync(OpPong, json, ct);
        }
    }
}