using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SwitchPresence.Nintendo;

/// <summary>Raised for anything the caller might retry or back off from.</summary>
public sealed class ApiException(string message, HttpStatusCode? status = null, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>Honoured in preference to our own backoff schedule, as the API terms require.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal static class ApiHttp
{
    public static async Task<(HttpStatusCode Status, byte[] Body, string? ContentType, TimeSpan? RetryAfter)>
        RawAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        return (response.StatusCode,
                await response.Content.ReadAsByteArrayAsync(ct),
                response.Content.Headers.ContentType?.MediaType,
                retryAfter > TimeSpan.Zero ? retryAfter : null);
    }

    public static async Task<T> JsonAsync<T>(HttpClient http, HttpRequestMessage request,
        JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var (status, body, _, retryAfter) = await RawAsync(http, request, ct);
        var text = Text(body);
        if ((int)status is < 200 or > 299)
            throw new ApiException($"HTTP {(int)status} from {request.RequestUri}: {Clip(text)}", status, retryAfter);

        return Parse(text, typeInfo, request.RequestUri?.ToString());
    }

    public static T Parse<T>(string text, JsonTypeInfo<T> typeInfo, string? source)
    {
        try
        {
            return JsonSerializer.Deserialize(text, typeInfo)
                ?? throw new ApiException($"null JSON from {source}");
        }
        catch (JsonException ex)
        {
            throw new ApiException($"unparseable JSON from {source}: {ex.Message}: {Clip(text)}");
        }
    }

    /// <summary>UTF-8 with any BOM stripped; System.Text.Json rejects a leading BOM.</summary>
    public static string Text(byte[] bytes)
    {
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    private static string Clip(string s)
    {
        return s.Length <= 300 ? s : s[..300] + "...";
    }
}