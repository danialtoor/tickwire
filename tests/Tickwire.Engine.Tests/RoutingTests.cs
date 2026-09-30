using FluentAssertions;
using Tickwire.Pricing;
using Tickwire.Venue;

namespace Tickwire.Engine.Tests;

public class OrderRouterTests
{
    private static VenueTop Top(Exchange e, decimal? bid, decimal? ask, decimal size = 10) =>
        new(e, bid is { } b ? new BookLevel(b, size, 1) : null, ask is { } a ? new BookLevel(a, size, 1) : null);

    [Fact]
    public void Marketable_buy_goes_to_the_best_price_after_fees()
    {
        var decision = OrderRouter.Route(
            [Top(Exchanges.Primary, 5.00m, 5.10m), Top(Exchanges.Nova, 5.00m, 5.11m), Top(Exchanges.Argo, 5.00m, 5.09m)],
            Side.Buy, OrderType.Limit, 5.20m);

        decision.Exchange.Should().Be(Exchanges.Argo, "5.09 + 0.0065 fee beats 5.10 + 0.005 and 5.11 + 0.0015");
        decision.Reason.Should().StartWith("Routed to ARGO");
    }

    [Fact]
    public void Fees_break_a_tie_in_displayed_price()
    {
        var decision = OrderRouter.Route(
            [Top(Exchanges.Primary, 5.00m, 5.10m), Top(Exchanges.Nova, 5.00m, 5.10m), Top(Exchanges.Argo, 5.00m, 5.10m)],
            Side.Buy, OrderType.Market, null);

        decision.Exchange.Should().Be(Exchanges.Nova, "the cheapest taker fee wins at the same displayed price");
    }

    [Fact]
    public void Marketable_sell_goes_to_the_highest_bid_net_of_fees()
    {
        var decision = OrderRouter.Route(
            [Top(Exchanges.Primary, 5.00m, 5.20m), Top(Exchanges.Nova, 4.99m, 5.20m), Top(Exchanges.Argo, 5.00m, 5.20m)],
            Side.Sell, OrderType.Limit, 4.90m);

        decision.Exchange.Should().Be(Exchanges.Primary, "5.00 - 0.005 beats 5.00 - 0.0065 and 4.99 - 0.0015");
    }

    [Fact]
    public void Only_exchanges_inside_the_limit_count()
    {
        var decision = OrderRouter.Route(
            [Top(Exchanges.Primary, 5.00m, 5.10m), Top(Exchanges.Nova, 5.00m, 5.30m)],
            Side.Buy, OrderType.Limit, 5.10m);

        decision.Exchange.Should().Be(Exchanges.Primary);
    }

    [Fact]
    public void Orders_that_would_rest_go_where_posting_pays_best()
    {
        var decision = OrderRouter.Route(
            [Top(Exchanges.Primary, 5.00m, 5.10m), Top(Exchanges.Nova, 5.00m, 5.10m), Top(Exchanges.Argo, 5.00m, 5.10m)],
            Side.Buy, OrderType.Limit, 5.02m);

        decision.Exchange.Should().Be(Exchanges.Argo, "it has the biggest maker rebate");
        decision.Reason.Should().Contain("not marketable");
    }
}

/// <summary>The OMS on the full three-exchange market: routing, directed orders, fees and the consolidated quote.</summary>
public sealed class RoutingTests : IAsyncLifetime
{
    private readonly MarketDataCache _cache = new();
    private readonly RecordingListener _listener = new();
    private SimulatedVenue _venue = null!;
    private OrderManager _oms = null!;
    private readonly ClientAccount _alice = new("alice", "Alice", new RiskLimits());
    private OptionContract _atmCall = null!;

    public async Task InitializeAsync()
    {
        var registry = InstrumentRegistry.Build(new DateOnly(2025, 3, 10));
        _venue = new SimulatedVenue(registry, new VenueOptions { EnableNoiseTraders = false }, _cache,
            new FixedClock(new DateTime(2025, 3, 10, 15, 0, 0, DateTimeKind.Utc)));
        _oms = new OrderManager(new SimulatedVenueAdapter(_venue), _cache);
        _oms.AddListener(_listener);
        _venue.SetSink(_oms);
        _venue.Start(withTimers: false);
        _oms.Start();
        await _venue.FlushAsync();
        _atmCall = registry.Find("SPY", registry.Chain("SPY")[0].Expiry, OptionRight.Call, 560)!;
    }

    public async Task DisposeAsync()
    {
        await _oms.DisposeAsync();
        await _venue.DisposeAsync();
    }

    private QuoteSnapshot Quote => _cache.Quote(_atmCall.Id)!;

    private async Task SettleAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await _oms.FlushAsync();
            await _venue.FlushAsync();
        }
    }

    private NewOrderRequest Buy(string clOrdId, decimal price, decimal qty, string? destination = null) =>
        new(_alice, clOrdId, _atmCall, null, Side.Buy, OrderType.Limit, TimeInForce.ImmediateOrCancel, price, qty, null, 0)
        {
            Destination = destination,
        };

    [Fact]
    public void Consolidated_quote_is_the_best_of_every_exchange()
    {
        Quote.Venues.Select(v => v.Exchange).Should().Equal("TWX", "NOVA", "ARGO");
        Quote.Bid.Should().Be(Quote.Venues.Max(v => v.Bid));
        Quote.Ask.Should().Be(Quote.Venues.Min(v => v.Ask));
        Quote.Venues.Should().OnlyContain(v => v.Bid < v.Ask);
    }

    [Fact]
    public async Task Smart_order_fills_where_the_router_says_with_fees_and_liquidity_flag()
    {
        var q = Quote;
        var expected = OrderRouter.Route(
            [.. q.Venues.Select(v => new VenueTop(Exchanges.Find(v.Exchange)!, null, v.Ask is { } a ? new BookLevel(a, v.AskSize, 1) : null))],
            Side.Buy, OrderType.Limit, q.Ask);

        _oms.Submit(Buy("SOR-1", q.Ask!.Value, 1));
        await SettleAsync();

        var reports = _listener.For("SOR-1");
        reports.Single(r => r.ExecType == ExecType.New).RouteReason.Should().StartWith($"Routed to {expected.Exchange.Code}");
        var fill = reports.Single(r => r.ExecType == ExecType.Trade);
        fill.LastMkt.Should().Be(expected.Exchange.Code);
        fill.LastPx.Should().Be(q.Ask);
        fill.LastLiquidity.Should().Be(Liquidity.Removed);
        fill.Commission.Should().Be(expected.Exchange.TakerFee);
        fill.Order.Exchange.Should().Be(expected.Exchange.Code);
    }

    [Fact]
    public async Task Directed_order_goes_where_it_is_told()
    {
        var nova = Quote.Venues.Single(v => v.Exchange == "NOVA");
        _oms.Submit(Buy("DIR-1", nova.Ask!.Value, 2, destination: "NOVA"));
        await SettleAsync();

        var reports = _listener.For("DIR-1");
        reports.Single(r => r.ExecType == ExecType.New).RouteReason.Should().Be("Directed to NOVA (ExDestination)");
        reports.Where(r => r.ExecType == ExecType.Trade).Should().OnlyContain(r => r.LastMkt == "NOVA" && r.LastPx == nova.Ask);
        reports.Last().Order.Fees.Should().Be(2 * Exchanges.Nova.TakerFee);
    }

    [Fact]
    public async Task Resting_order_earns_the_maker_rebate_when_it_is_hit()
    {
        var q = Quote;
        var price = q.Bid!.Value + _atmCall.TickSize; // improves the NBBO bid: rests at the best-rebate exchange
        _oms.Submit(new NewOrderRequest(_alice, "REST-1", _atmCall, null, Side.Buy, OrderType.Limit, TimeInForce.Day, price, 3, null, 0));
        await SettleAsync();
        _listener.For("REST-1").Single(r => r.ExecType == ExecType.New).Order.Exchange.Should().Be("ARGO");

        var bob = new ClientAccount("bob", "Bob", new RiskLimits());
        _oms.Submit(new NewOrderRequest(bob, "HIT-1", _atmCall, null, Side.Sell, OrderType.Limit, TimeInForce.ImmediateOrCancel, price, 3,
            null, 0));
        await SettleAsync();

        var fill = _listener.For("REST-1").Single(r => r.ExecType == ExecType.Trade);
        fill.LastMkt.Should().Be("ARGO");
        fill.LastLiquidity.Should().Be(Liquidity.Added);
        fill.Commission.Should().Be(3 * Exchanges.Argo.MakerFee, "a rebate is a negative fee");
    }
}

internal sealed class FixedClock(DateTime now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
