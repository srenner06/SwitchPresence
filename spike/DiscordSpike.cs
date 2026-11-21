// Milestone 2 spike: Discord local IPC. Throwaway, same as the Nintendo one.
//   dotnet run --project spike -- discord [imageUrl] [--switch1]
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DiscordSpike
{
    private const string SwitchAppId = "1573788454590873731";
    private const string Switch2AppId = "1573788516343873598";

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var switch1 = args.Contains("--switch1");
        var appId = switch1 ? SwitchAppId : Switch2AppId;
        var imageUrl = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"app id {appId} ({(switch1 ? "Nintendo Switch" : "Nintendo Switch 2")})");
        Console.WriteLine(imageUrl is null
            ? "no image url given - assets omitted (pass one to test large_image)"
            : $"large_image {imageUrl}");

        using var pipe = await ConnectAsync();

        // --- handshake ---
        await SendAsync(pipe, 0, JsonSerializer.Serialize(new { v = 1, client_id = appId }, Opts));
        var (op, json) = await NextAsync(pipe);
        Console.WriteLine($"\n<- op {op}: {json}");
        if (op != 1)
            return Fail("handshake did not come back as op 1 (FRAME)");

        using (var ready = JsonDocument.Parse(json))
        {
            if (ready.RootElement.TryGetProperty("evt", out var evt) && evt.GetString() == "READY")
                Console.WriteLine("handshake OK");
            else
                return Fail("expected evt READY - Discord rejected the client id or the handshake");
        }

        // --- set activity ---
        object? assets = imageUrl is null ? null
            : new { large_image = imageUrl, large_text = "Playing on Nintendo Switch 2" };

        var activity = new
        {
            type = 0,
            details = "Nintendo Switch Sports Resort",
            state = "Playing online",
            status_display_type = 2,   // show the game name, not the app name
            timestamps = new { start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            assets,
            buttons = new[]
            {
                new { label = "Nintendo eShop", url = "https://ec.nintendo.com/apps/04008e001f098000/CH" },
            },
        };

        Console.WriteLine("\n-> SET_ACTIVITY");
        await SendAsync(pipe, 1, JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            nonce = Guid.NewGuid().ToString(),
            args = new { pid = Environment.ProcessId, activity },
        }, Opts));
        (op, json) = await NextAsync(pipe);
        Console.WriteLine($"<- op {op}: {json}");
        if (json.Contains("\"evt\":\"ERROR\""))
            return Fail("Discord rejected the activity - see the message above");

        Console.WriteLine("""

            Check your own Discord profile now. Expected:
              - the big label is the APP name (Nintendo Switch 2)
              - "Nintendo Switch Sports Resort" as the first line
              - "Playing online" under it
              - an elapsed timer that started just now  <- if it reads years, timestamps are seconds, not ms
              - a "Nintendo eShop" button
              - in the member list, the game name rather than the app name (status_display_type)
            """);
        Console.Write("\nPress Enter to clear the activity > ");
        Console.ReadLine();

        // --- clear ---
        Console.WriteLine("-> SET_ACTIVITY (null)");
        await SendAsync(pipe, 1, JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            nonce = Guid.NewGuid().ToString(),
            args = new { pid = Environment.ProcessId, activity = (object?)null },
        }, Opts));
        (op, json) = await NextAsync(pipe);
        Console.WriteLine($"<- op {op}: {json}");
        Console.WriteLine("\nCleared. Confirm the activity is gone from your profile.");
        return 0;
    }

    private static int Fail(string why)
    {
        Console.Error.WriteLine($"FAILED: {why}");
        return 1;
    }

    private static async Task<NamedPipeClientStream> ConnectAsync()
    {
        for (var i = 0; i <= 9; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}",
                PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500);
                Console.WriteLine($"connected to \\\\.\\pipe\\discord-ipc-{i}");
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
            }
        }

        throw new Exception("No discord-ipc-N pipe found. Is the Discord DESKTOP app running? "
            + "The browser version has no local pipe.");
    }

    // Answers PINGs so a slow look at the profile doesn't drop the connection.
    private static async Task<(int Op, string Json)> NextAsync(Stream s)
    {
        while (true)
        {
            var (op, json) = await ReadAsync(s);
            if (op != 3)
                return (op, json);

            await SendAsync(s, 4, json);
            Console.WriteLine("    (ping -> pong)");
        }
    }

    private static async Task SendAsync(Stream s, int op, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, op);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), payload.Length);
        await s.WriteAsync(header);
        await s.WriteAsync(payload);
        await s.FlushAsync();
    }

    private static async Task<(int Op, string Json)> ReadAsync(Stream s)
    {
        var header = new byte[8];
        await s.ReadExactlyAsync(header);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var payload = new byte[len];
        await s.ReadExactlyAsync(payload);
        return (op, Encoding.UTF8.GetString(payload));
    }
}