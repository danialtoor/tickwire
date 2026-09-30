using MessagePack;

namespace Tickwire.MarketData.Providers;

/// <summary>
/// Alpaca options stream. Protocol (public docs): <c>wss://stream.data.alpaca.markets/v1beta1/{indicative|opra}</c>,
/// MessagePack-encoded (the options stream doesn't offer JSON). Send
/// <c>{"action":"auth","key","secret"}</c>, then <c>{"action":"subscribe","quotes":[...],"trades":[...]}</c>.
/// Messages are arrays of maps with <c>T</c> = "q" (quote: S, bp, bs, ap, as, t) or "t" (trade: S, p, s, t).
/// The "indicative" feed is free with an Alpaca account; "opra" needs a paid subscription.
/// </summary>
public sealed class AlpacaProvider : IOptionFeedProvider
{
    public ProviderInfo Info { get; } = new(
        "alpaca",
        "Alpaca",
        "Options quotes and trades: free indicative feed or full OPRA.",
        "WebSocket (MessagePack)",
        "https://docs.alpaca.markets/docs/real-time-option-data",
        "https://app.alpaca.markets/signup",
        [
            new("keyId", "API key ID", false),
            new("secretKey", "Secret key", true),
            new("feed", "Feed (indicative or opra)", false, "indicative", "indicative"),
        ],
        ["NBBO quotes", "Trades"],
        "#fcd535",
        Verified: false);

    public async Task RunAsync(IReadOnlyDictionary<string, string> credentials, FeedSubscription subscription, IFeedSink sink,
        CancellationToken cancellationToken)
    {
        var key = Required.Get(credentials, "keyId");
        var secret = Required.Get(credentials, "secretKey");
        var feed = credentials.GetValueOrDefault("feed")?.Trim().ToLowerInvariant() == "opra" ? "opra" : "indicative";

        await using var ws = new FeedSocket();
        ws.Options.SetRequestHeader("Content-Type", "application/msgpack");
        await ws.ConnectAsync(new Uri($"wss://stream.data.alpaca.markets/v1beta1/{feed}"), cancellationToken).ConfigureAwait(false);
        await ws.SendBinaryAsync(Pack(new Dictionary<string, object> { ["action"] = "auth", ["key"] = key, ["secret"] = secret }),
            cancellationToken).ConfigureAwait(false);

        var subscribed = false;
        while (await ws.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } bytes)
        {
            foreach (var msg in Parse(bytes))
            {
                switch (msg)
                {
                    case Control { Kind: "success", Message: "authenticated" } when !subscribed:
                        subscribed = true;
                        var symbols = subscription.Contracts.Select(Symbology.Compact).ToArray();
                        await ws.SendBinaryAsync(Pack(new Dictionary<string, object>
                        {
                            ["action"] = "subscribe",
                            ["quotes"] = symbols,
                            ["trades"] = symbols,
                        }), cancellationToken).ConfigureAwait(false);
                        sink.OnStatus($"Authenticated with Alpaca ({feed}); subscribed to {symbols.Length} contracts");
                        break;
                    case Control { Kind: "error" } err:
                        throw new FeedAuthException($"Alpaca: {err.Message}");
                    case QuoteMsg q:
                        sink.OnQuote(q.Quote);
                        break;
                    default:
                        break;
                }
            }
        }

        throw new IOException("Alpaca closed the stream");
    }

    internal static byte[] Pack(Dictionary<string, object> payload) => MessagePackSerializer.Serialize(payload,
        MessagePack.Resolvers.ContractlessStandardResolver.Options);

    internal abstract record Msg;

    internal sealed record Control(string Kind, string? Message) : Msg;

    internal sealed record QuoteMsg(ExternalQuote Quote) : Msg;

    internal static IEnumerable<Msg> Parse(byte[] bytes)
    {
        var decoded = MessagePackSerializer.Deserialize<object>(bytes, MessagePack.Resolvers.ContractlessStandardResolver.Options);
        var items = decoded is object[] arr ? arr : [decoded];
        var result = new List<Msg>();
        foreach (var item in items)
        {
            if (item is not IDictionary<object, object> m)
            {
                continue;
            }

            string? S(string k) => m.TryGetValue(k, out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
            decimal? D(string k) => m.TryGetValue(k, out var v) && v is not null
                ? Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture)
                : null;
            var time = m.TryGetValue("t", out var t) && t is DateTime dt ? dt.ToUniversalTime() : DateTime.UtcNow;

            switch (S("T"))
            {
                case "success" or "error" or "subscription":
                    result.Add(new Control(S("T")!, S("msg")));
                    break;
                case "q" when Symbology.TryParse(S("S"), out var occ):
                    result.Add(new QuoteMsg(new ExternalQuote(occ.ToString(), D("bp"), D("ap"), D("bs"), D("as"), null, null, null, time, "alpaca")));
                    break;
                case "t" when Symbology.TryParse(S("S"), out var occ):
                    result.Add(new QuoteMsg(new ExternalQuote(occ.ToString(), null, null, null, null, D("p"), null, null, time, "alpaca")));
                    break;
                default:
                    break;
            }
        }

        return result;
    }
}
