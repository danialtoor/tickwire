using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Tickwire.MarketData;

/// <summary>Minimal client WebSocket wrapper: send JSON or bytes, read whole messages.</summary>
internal sealed class FeedSocket : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();

    public ClientWebSocketOptions Options => _ws.Options;

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _ws.ConnectAsync(uri, ct);

    public Task SendJsonAsync(object payload, CancellationToken ct) =>
        _ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, ct);

    public Task SendBinaryAsync(byte[] payload, CancellationToken ct) =>
        _ws.SendAsync(payload, WebSocketMessageType.Binary, true, ct);

    /// <summary>Next complete message, or null when the server closed the socket.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return ms.ToArray();
            }
        }
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken ct) =>
        await ReceiveAsync(ct).ConfigureAwait(false) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
        }

        _ws.Dispose();
    }
}

internal static class Json
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    public static decimal? Dec(this JsonElement e, string name) => Symbology.ParseDecimal(e.Str(name));

    public static double? Dbl(this JsonElement e, string name) => (double?)e.Dec(name);

    public static long? Long(this JsonElement e, string name) =>
        long.TryParse(e.Str(name), System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : null;

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>Unix time in milliseconds, microseconds or nanoseconds, picked by magnitude.</summary>
    public static DateTime FromUnix(long? value, DateTime fallback)
    {
        if (value is not { } v || v <= 0)
        {
            return fallback;
        }

        return v switch
        {
            < 100_000_000_000L => DateTime.UnixEpoch.AddSeconds(v),
            < 100_000_000_000_000L => DateTime.UnixEpoch.AddMilliseconds(v),
            < 100_000_000_000_000_000L => DateTime.UnixEpoch.AddTicks(v * 10),
            _ => DateTime.UnixEpoch.AddTicks(v / 100),
        };
    }
}
