using System.Globalization;
using System.Text.Json;
using Tickwire.Pricing;

namespace Tickwire.MarketData.Providers;

/// <summary>
/// SpiderRock MLink (JSON over WebSocket). Connects to the MLink JSON endpoint with the API key, subscribes to
/// <c>OptionNbboQuote</c> (NBBO) and <c>LiveImpliedQuote</c> (implied vol and delta) for each underlying, plus
/// <c>StockBookQuote</c> for the underlying price. Messages carry a <c>header.mTyp</c> and a <c>message</c> whose
/// <c>pkey.okey</c> identifies the option (tk ticker, dt expiry, xx strike, cp call/put).
/// Field names follow the MLink message catalogue; the parser accepts common variants because MLink versions differ.
/// </summary>
public sealed class SpiderRockProvider : IOptionFeedProvider
{
    public ProviderInfo Info { get; } = new(
        "spiderrock",
        "SpiderRock",
        "MLink API: option NBBO plus live implied volatility and greeks.",
        "WebSocket (MLink JSON)",
        "https://docs.spiderrockconnect.com/",
        "https://www.spiderrock.net/",
        [
            new("apiKey", "MLink API key", true),
            new("environment", "Environment (live or delayed)", false, "delayed", "delayed"),
        ],
        ["NBBO quotes", "Implied vol", "Delta", "Underlying price"],
        "#e2e8f0",
        Verified: false);

    public async Task RunAsync(IReadOnlyDictionary<string, string> credentials, FeedSubscription subscription, IFeedSink sink,
        CancellationToken cancellationToken)
    {
        var key = Required.Get(credentials, "apiKey");
        var env = credentials.GetValueOrDefault("environment")?.Trim().ToLowerInvariant() == "live" ? "mlink-live" : "mlink-delay";
        await using var ws = new FeedSocket();
        await ws.ConnectAsync(new Uri($"wss://{env}.nms.saturn.spiderrockconnect.com/mlink/json?apiKey={Uri.EscapeDataString(key)}"),
            cancellationToken).ConfigureAwait(false);

        foreach (var tk in subscription.Underlyings)
        {
            foreach (var msgName in new[] { "OptionNbboQuote", "LiveImpliedQuote" })
            {
                await ws.SendJsonAsync(new
                {
                    header = new { mTyp = "MLinkSubscribe" },
                    message = new { msgName, where = $"okey:tk:eq:{tk}", activeLatency = 500 },
                }, cancellationToken).ConfigureAwait(false);
            }

            await ws.SendJsonAsync(new
            {
                header = new { mTyp = "MLinkSubscribe" },
                message = new { msgName = "StockBookQuote", where = $"ticker:tk:eq:{tk}", activeLatency = 500 },
            }, cancellationToken).ConfigureAwait(false);
        }

        sink.OnStatus($"Subscribed to MLink for {string.Join(", ", subscription.Underlyings)}");
        var wanted = subscription.Contracts.Select(c => c.ToString()).ToHashSet(StringComparer.Ordinal);
        while (await ws.ReceiveTextAsync(cancellationToken).ConfigureAwait(false) is { } text)
        {
            Handle(text, wanted, sink);
        }

        throw new IOException("SpiderRock MLink closed the connection");
    }

    internal static void Handle(string text, HashSet<string> wanted, IFeedSink sink)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        foreach (var item in items)
        {
            var type = item.Obj("header")?.Str("mTyp");
            var m = item.Obj("message") ?? item;
            switch (type)
            {
                case "MLinkAdmin" or "MLinkSubscribeAck" when m.Str("result") is { } result && result.Contains("error", StringComparison.OrdinalIgnoreCase):
                    throw new FeedAuthException($"SpiderRock MLink: {m.Str("detail") ?? result}");
                case "OptionNbboQuote" when Occ(m) is { } occ && wanted.Contains(occ):
                    sink.OnQuote(new ExternalQuote(occ, First(m, "bidPrice", "bidPrice1", "bidPx"), First(m, "askPrice", "askPrice1", "askPx"),
                        First(m, "bidSize", "bidSize1", "cumBidSize1"), First(m, "askSize", "askSize1", "cumAskSize1"), null, null, null,
                        DateTime.UtcNow, "spiderrock"));
                    break;
                case "LiveImpliedQuote" when Occ(m) is { } occ && wanted.Contains(occ):
                    sink.OnQuote(new ExternalQuote(occ, null, null, null, null, null,
                        (double?)First(m, "ivol", "vol", "midVol", "surfVol"), (double?)First(m, "de", "delta"), DateTime.UtcNow, "spiderrock"));
                    break;
                case "StockBookQuote" when m.Obj("pkey")?.Obj("ticker")?.Str("tk") is { } tk:
                    var bid = First(m, "bidPrice1", "bidPrice");
                    var ask = First(m, "askPrice1", "askPrice");
                    if (bid is { } b && ask is { } a && a > 0)
                    {
                        sink.OnUnderlying(new ExternalUnderlying(tk, (b + a) / 2, DateTime.UtcNow, "spiderrock"));
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private static decimal? First(JsonElement m, params string[] names)
    {
        foreach (var n in names)
        {
            if (m.Dec(n) is { } v)
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>Builds the OCC symbol from an MLink option key (okey: tk, dt yyyy-MM-dd, xx strike, cp Call/Put).</summary>
    internal static string? Occ(JsonElement m)
    {
        var okey = m.Obj("pkey")?.Obj("okey") ?? m.Obj("okey");
        if (okey is not { } k || k.Str("tk") is not { } tk || k.Dec("xx") is not { } strike || k.Str("cp") is not { } cp
            || !DateOnly.TryParse(k.Str("dt"), CultureInfo.InvariantCulture, out var expiry))
        {
            return null;
        }

        var right = cp.StartsWith('C') || cp.StartsWith('c') ? OptionRight.Call : OptionRight.Put;
        return new OccSymbol(tk.ToUpperInvariant(), expiry, right, strike).ToString();
    }
}
