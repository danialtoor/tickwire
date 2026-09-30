using System.Net.Http.Headers;
using System.Text.Json;

namespace Tickwire.MarketData.Providers;

/// <summary>
/// Tradier market data streaming. Protocol (public docs): <c>POST /v1/markets/events/session</c> with the access token
/// returns a session id; connect to <c>wss://ws.tradier.com/v1/markets/events</c> and send
/// <c>{"symbols":[...],"sessionid":ID,"filter":["quote","trade"]}</c>. Messages are JSON objects with a <c>type</c>.
/// Streaming needs a Tradier brokerage account token (sandbox tokens don't stream).
/// </summary>
public sealed class TradierProvider(HttpClient? http = null) : IOptionFeedProvider
{
    private readonly HttpClient _http = http ?? new HttpClient();

    public ProviderInfo Info { get; } = new(
        "tradier",
        "Tradier",
        "Brokerage API with streaming quotes for equities and options.",
        "HTTPS session + WebSocket (JSON)",
        "https://documentation.tradier.com/brokerage-api/streaming/wss-market-websocket",
        "https://tradier.com/",
        [new("accessToken", "Access token", true)],
        ["NBBO quotes", "Trades", "Underlying price"],
        "#2c7be5",
        Verified: false);

    public async Task RunAsync(IReadOnlyDictionary<string, string> credentials, FeedSubscription subscription, IFeedSink sink,
        CancellationToken cancellationToken)
    {
        var token = Required.Get(credentials, "accessToken");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tradier.com/v1/markets/events/session");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new FeedAuthException($"Tradier refused the session request ({(int)resp.StatusCode})");
        }

        using var session = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var sessionId = session.RootElement.Obj("stream")?.Str("sessionid") ?? throw new FeedAuthException("Tradier returned no stream session");
        sink.OnStatus("Tradier streaming session created");

        await using var ws = new FeedSocket();
        await ws.ConnectAsync(new Uri("wss://ws.tradier.com/v1/markets/events"), cancellationToken).ConfigureAwait(false);
        var symbols = subscription.Underlyings.Concat(subscription.Contracts.Select(Symbology.Compact)).ToArray();
        await ws.SendJsonAsync(new { symbols, sessionid = sessionId, filter = new[] { "quote", "trade" }, linebreak = true },
            cancellationToken).ConfigureAwait(false);
        sink.OnStatus($"Subscribed to {symbols.Length} symbols");

        while (await ws.ReceiveTextAsync(cancellationToken).ConfigureAwait(false) is { } text)
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Handle(line, sink);
            }
        }

        throw new IOException("Tradier closed the stream");
    }

    internal static void Handle(string line, IFeedSink sink)
    {
        using var doc = JsonDocument.Parse(line);
        var e = doc.RootElement;
        var symbol = e.Str("symbol");
        var now = DateTime.UtcNow;
        switch (e.Str("type"))
        {
            case "quote" when Symbology.TryParse(symbol, out var occ):
                sink.OnQuote(new ExternalQuote(occ.ToString(), e.Dec("bid"), e.Dec("ask"), e.Dec("bidsz"), e.Dec("asksz"), null, null, null,
                    Json.FromUnix(e.Long("biddate") ?? e.Long("askdate"), now), "tradier"));
                break;
            case "trade" when Symbology.TryParse(symbol, out var occ):
                sink.OnQuote(new ExternalQuote(occ.ToString(), null, null, null, null, e.Dec("price") ?? e.Dec("last"), null, null,
                    Json.FromUnix(e.Long("date"), now), "tradier"));
                break;
            case "trade" when symbol is not null && e.Dec("price") is { } px:
                sink.OnUnderlying(new ExternalUnderlying(symbol, px, Json.FromUnix(e.Long("date"), now), "tradier"));
                break;
            case "error":
                throw new FeedAuthException($"Tradier: {e.Str("error") ?? line}");
            default:
                break;
        }
    }
}
