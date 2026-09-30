using System.Text.Json;
using Tickwire.Pricing;

namespace Tickwire.MarketData.Providers;

/// <summary>
/// Polygon.io options WebSocket. Protocol (public docs): connect, send
/// <c>{"action":"auth","params":KEY}</c>, then <c>{"action":"subscribe","params":"Q.O:SPY...,T.O:SPY..."}</c>.
/// Quotes arrive as arrays of <c>{"ev":"Q","sym":"O:...","bp","bs","ap","as","t"}</c>; trades as <c>{"ev":"T","p","s"}</c>.
/// Option quotes need a plan that includes them; the delayed endpoint serves 15-minute delayed data.
/// </summary>
public sealed class PolygonProvider : IOptionFeedProvider
{
    public ProviderInfo Info { get; } = new(
        "polygon",
        "Polygon.io",
        "Real-time and delayed OPRA quotes and trades over WebSocket.",
        "WebSocket (JSON)",
        "https://polygon.io/docs/websocket/options/overview",
        "https://polygon.io/dashboard/signup",
        [
            new("apiKey", "API key", true),
            new("feed", "Feed (realtime or delayed)", false, "delayed", "delayed"),
        ],
        ["NBBO quotes", "Trades"],
        "#7c5cff",
        Verified: false);

    public async Task RunAsync(IReadOnlyDictionary<string, string> credentials, FeedSubscription subscription, IFeedSink sink,
        CancellationToken cancellationToken)
    {
        var key = Required.Get(credentials, "apiKey");
        var host = credentials.GetValueOrDefault("feed")?.Trim().ToLowerInvariant() == "realtime" ? "socket.polygon.io" : "delayed.polygon.io";
        await using var ws = new FeedSocket();
        await ws.ConnectAsync(new Uri($"wss://{host}/options"), cancellationToken).ConfigureAwait(false);
        await ws.SendJsonAsync(new { action = "auth", @params = key }, cancellationToken).ConfigureAwait(false);

        var authed = false;
        while (await ws.ReceiveTextAsync(cancellationToken).ConfigureAwait(false) is { } text)
        {
            foreach (var ev in Parse(text))
            {
                switch (ev)
                {
                    case Status { Value: "auth_success" }:
                        authed = true;
                        sink.OnStatus("Authenticated with Polygon.io");
                        // Polygon accepts a comma-separated list; keep each subscribe message a reasonable size.
                        foreach (var chunk in subscription.Contracts.Chunk(200))
                        {
                            var syms = string.Join(',', chunk.SelectMany(c => new[] { $"Q.{Symbology.Polygon(c)}", $"T.{Symbology.Polygon(c)}" }));
                            await ws.SendJsonAsync(new { action = "subscribe", @params = syms }, cancellationToken).ConfigureAwait(false);
                        }

                        sink.OnStatus($"Subscribed to {subscription.Contracts.Count} contracts");
                        break;
                    case Status { Value: "auth_failed" } s:
                        throw new FeedAuthException($"Polygon.io rejected the API key: {s.Message}");
                    case Status s when !authed && s.Value is not "connected":
                        sink.OnStatus($"Polygon.io: {s.Value} {s.Message}".Trim());
                        break;
                    case QuoteEvent q:
                        sink.OnQuote(q.Quote);
                        break;
                    default:
                        break;
                }
            }
        }

        throw new IOException("Polygon.io closed the connection");
    }

    internal abstract record Event;

    internal sealed record Status(string Value, string? Message) : Event;

    internal sealed record QuoteEvent(ExternalQuote Quote) : Event;

    internal static IEnumerable<Event> Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        var result = new List<Event>();
        foreach (var e in items)
        {
            var now = DateTime.UtcNow;
            switch (e.Str("ev"))
            {
                case "status":
                    result.Add(new Status(e.Str("status") ?? "", e.Str("message")));
                    break;
                case "Q" when Symbology.TryParse(e.Str("sym"), out var occ):
                    result.Add(new QuoteEvent(new ExternalQuote(occ.ToString(), e.Dec("bp"), e.Dec("ap"), e.Dec("bs"), e.Dec("as"), null, null,
                        null, Json.FromUnix(e.Long("t"), now), "polygon")));
                    break;
                case "T" when Symbology.TryParse(e.Str("sym"), out var occ):
                    result.Add(new QuoteEvent(new ExternalQuote(occ.ToString(), null, null, null, null, e.Dec("p"), null, null,
                        Json.FromUnix(e.Long("t"), now), "polygon")));
                    break;
                default:
                    break;
            }
        }

        return result;
    }
}
