using System.Collections.Concurrent;
using FluentAssertions;
using Tickwire.MarketData.Providers;
using Tickwire.Pricing;

namespace Tickwire.MarketData.Tests;

internal sealed class Sink : IFeedSink
{
    public ConcurrentQueue<ExternalQuote> Quotes { get; } = new();
    public ConcurrentQueue<ExternalUnderlying> Underlyings { get; } = new();
    public ConcurrentQueue<string> Status { get; } = new();

    public void OnQuote(ExternalQuote quote) => Quotes.Enqueue(quote);

    public void OnUnderlying(ExternalUnderlying underlying) => Underlyings.Enqueue(underlying);

    public void OnStatus(string message) => Status.Enqueue(message);
}

// Payloads below follow each vendor's published message formats. They check that parsing and symbol mapping are
// right; they can't prove a live connection works, since that needs paid credentials.
public class ProviderParsingTests
{
    private static readonly OccSymbol Call = new("SPY", new DateOnly(2026, 10, 2), OptionRight.Call, 560m);
    private static readonly string CallOcc = Call.ToString();

    [Fact]
    public void Symbology_round_trips_every_vendor_format()
    {
        Symbology.Compact(Call).Should().Be("SPY261002C00560000");
        Symbology.Polygon(Call).Should().Be("O:SPY261002C00560000");
        Symbology.Occ21(Call).Should().Be("SPY   261002C00560000");
        foreach (var s in new[] { "SPY261002C00560000", "O:SPY261002C00560000", "SPY   261002C00560000" })
        {
            Symbology.TryParse(s, out var parsed).Should().BeTrue(s);
            parsed.Should().Be(Call);
        }

        Symbology.TryParse("SPY", out _).Should().BeFalse();
    }

    [Fact]
    public void Polygon_status_and_quotes()
    {
        var events = PolygonProvider.Parse("""
            [{"ev":"status","status":"auth_success","message":"authenticated"},
             {"ev":"Q","sym":"O:SPY261002C00560000","bx":302,"bp":4.35,"bs":12,"ax":303,"ap":4.40,"as":8,"t":1790000000000},
             {"ev":"T","sym":"O:SPY261002C00560000","p":4.38,"s":2,"t":1790000000500}]
            """).ToList();

        events[0].Should().Be(new PolygonProvider.Status("auth_success", "authenticated"));
        var quote = ((PolygonProvider.QuoteEvent)events[1]).Quote;
        quote.OccSymbol.Should().Be(CallOcc);
        (quote.Bid, quote.Ask, quote.BidSize, quote.AskSize).Should().Be((4.35m, 4.40m, 12m, 8m));
        quote.Time.Should().Be(DateTime.UnixEpoch.AddMilliseconds(1790000000000));
        ((PolygonProvider.QuoteEvent)events[2]).Quote.Last.Should().Be(4.38m);
    }

    [Fact]
    public void Tradier_quotes_trades_and_underlying()
    {
        var sink = new Sink();
        TradierProvider.Handle("""{"type":"quote","symbol":"SPY261002C00560000","bid":4.35,"bidsz":12,"ask":4.4,"asksz":8,"biddate":"1790000000000"}""", sink);
        TradierProvider.Handle("""{"type":"trade","symbol":"SPY261002C00560000","price":"4.38","size":"2","date":"1790000000500"}""", sink);
        TradierProvider.Handle("""{"type":"trade","symbol":"SPY","price":"561.25","size":"100","date":"1790000000500"}""", sink);

        sink.Quotes.Should().HaveCount(2);
        sink.Quotes.First().Should().Match<ExternalQuote>(q => q.OccSymbol == CallOcc && q.Bid == 4.35m && q.Ask == 4.4m);
        sink.Quotes.Last().Last.Should().Be(4.38m);
        sink.Underlyings.Single().Should().Match<ExternalUnderlying>(u => u.Symbol == "SPY" && u.Price == 561.25m);

        var error = () => TradierProvider.Handle("""{"type":"error","error":"session expired"}""", sink);
        error.Should().Throw<FeedAuthException>().WithMessage("*session expired*");
    }

    [Fact]
    public void Alpaca_msgpack_control_and_quotes()
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(new object[]
        {
            new Dictionary<string, object> { ["T"] = "success", ["msg"] = "authenticated" },
            new Dictionary<string, object>
            {
                ["T"] = "q", ["S"] = "SPY261002C00560000", ["bp"] = 4.35, ["bs"] = 12, ["ap"] = 4.40, ["as"] = 8,
                ["t"] = new DateTime(2026, 9, 29, 14, 0, 0, DateTimeKind.Utc),
            },
            new Dictionary<string, object> { ["T"] = "error", ["code"] = 402, ["msg"] = "auth failed" },
        }, MessagePack.Resolvers.ContractlessStandardResolver.Options);

        var msgs = AlpacaProvider.Parse(bytes).ToList();

        msgs[0].Should().Be(new AlpacaProvider.Control("success", "authenticated"));
        var q = ((AlpacaProvider.QuoteMsg)msgs[1]).Quote;
        (q.OccSymbol, q.Bid, q.Ask).Should().Be((CallOcc, 4.35m, 4.40m));
        q.Time.Should().Be(new DateTime(2026, 9, 29, 14, 0, 0, DateTimeKind.Utc));
        msgs[2].Should().Be(new AlpacaProvider.Control("error", "auth failed"));
    }

    [Fact]
    public void Databento_cram_response_matches_the_documented_scheme()
    {
        // sha256("4f7c2a|<key>") in hex, a dash, then the key's last five characters.
        DatabentoProvider.CramResponse("4f7c2a", "db-ABCDEFGHIJKLMNOPQRSTUVWXYZ012")
            .Should().Be("7d2c6fcc042a423c18a435e903503fd7d0fafa63aeefc23a69ed0e38bf6319ba-YZ012");
        DatabentoProvider.Field("success=1|session_id=abc", "session_id").Should().Be("abc");
    }

    [Fact]
    public void Databento_maps_instruments_then_decodes_cbbo_records()
    {
        var sink = new Sink();
        var instruments = new Dictionary<ulong, string>();
        var wanted = new HashSet<string> { CallOcc };

        DatabentoProvider.Handle("""{"hd":{"ts_event":"2026-09-29T14:00:00.000000000Z","rtype":22,"publisher_id":0,"instrument_id":1234},"stype_in_symbol":"SPY.OPT","stype_out_symbol":"SPY   261002C00560000"}""",
            instruments, wanted, sink);
        DatabentoProvider.Handle("""{"hd":{"ts_event":"2026-09-29T14:00:01.000000000Z","rtype":195,"publisher_id":1,"instrument_id":1234},"levels":[{"bid_px":"4.35","ask_px":"4.40","bid_sz":12,"ask_sz":8}]}""",
            instruments, wanted, sink);
        DatabentoProvider.Handle("""{"hd":{"ts_event":"2026-09-29T14:00:01Z","rtype":195,"publisher_id":1,"instrument_id":1234},"levels":[{"bid_px":4350000000,"ask_px":4400000000,"bid_sz":1,"ask_sz":1}]}""",
            instruments, wanted, sink);
        DatabentoProvider.Handle("""{"hd":{"ts_event":"2026-09-29T14:00:01Z","rtype":195,"publisher_id":1,"instrument_id":999},"levels":[{"bid_px":"1","ask_px":"2"}]}""",
            instruments, wanted, sink);

        instruments[1234].Should().Be(CallOcc);
        sink.Quotes.Should().HaveCount(2, "unmapped instruments are skipped");
        sink.Quotes.Should().OnlyContain(q => q.Bid == 4.35m && q.Ask == 4.40m, "decimal and fixed-point prices decode the same");
    }

    [Fact]
    public void SpiderRock_nbbo_implied_and_stock_messages()
    {
        var sink = new Sink();
        var wanted = new HashSet<string> { CallOcc };
        SpiderRockProvider.Handle("""
            [{"header":{"mTyp":"OptionNbboQuote"},"message":{"pkey":{"okey":{"at":"EQT","ts":"NMS","tk":"SPY","dt":"2026-10-02","xx":560,"cp":"Call"}},"bidPrice":4.35,"askPrice":4.40,"bidSize":12,"askSize":8}},
             {"header":{"mTyp":"LiveImpliedQuote"},"message":{"pkey":{"okey":{"tk":"SPY","dt":"2026-10-02","xx":560,"cp":"Call"}},"ivol":0.152,"de":0.51}},
             {"header":{"mTyp":"StockBookQuote"},"message":{"pkey":{"ticker":{"at":"EQT","ts":"NMS","tk":"SPY"}},"bidPrice1":561.20,"askPrice1":561.30}}]
            """, wanted, sink);

        var quotes = sink.Quotes.ToList();
        quotes[0].Should().Match<ExternalQuote>(q => q.OccSymbol == CallOcc && q.Bid == 4.35m && q.Ask == 4.40m);
        quotes[1].ImpliedVol.Should().BeApproximately(0.152, 1e-9);
        quotes[1].Delta.Should().BeApproximately(0.51, 1e-9);
        sink.Underlyings.Single().Price.Should().Be(561.25m);
    }

    [Fact]
    public async Task Demo_feed_streams_quotes_around_the_reference()
    {
        var sink = new Sink();
        var provider = new DemoProvider(_ => (4.00m, 0.15, 0.5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1300));

        await provider.Invoking(p => p.RunAsync(new Dictionary<string, string>(), new FeedSubscription(["SPY"], [Call]), sink, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        sink.Quotes.Should().NotBeEmpty();
        sink.Quotes.Should().OnlyContain(q => q.Bid < q.Ask && q.Ask > 3.8m && q.Ask < 4.3m);
    }
}
