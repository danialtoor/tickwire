using System.Collections.Concurrent;
using FluentAssertions;
using Tickwire.Pricing;
using Tickwire.Venue;

namespace Tickwire.Engine.Tests;

/// <summary>Multi-leg orders against the real venue: makers on, noise traders and timers off.</summary>
public sealed class SpreadTests : IAsyncLifetime
{
    private readonly MarketDataCache _cache = new();
    private readonly RecordingListener _listener = new();
    private readonly ClientAccount _client = new("alice", "Alice", new RiskLimits());
    private InstrumentRegistry _registry = null!;
    private SimulatedVenue _venue = null!;
    private OrderManager _oms = null!;
    private OptionContract _c560 = null!, _c565 = null!, _p560 = null!;

    public async Task InitializeAsync()
    {
        _registry = InstrumentRegistry.Build(new DateOnly(2025, 3, 10));
        _venue = new SimulatedVenue(_registry, new VenueOptions { EnableNoiseTraders = false }, _cache,
            new FixedTime(new DateTime(2025, 3, 10, 15, 0, 0, DateTimeKind.Utc)));
        _oms = new OrderManager(new SimulatedVenueAdapter(_venue), _cache);
        _oms.AddListener(_listener);
        _venue.SetSink(_oms);
        _venue.Start(withTimers: false);
        _oms.Start();
        await _venue.FlushAsync();
        var expiry = _registry.Chain("SPY")[0].Expiry;
        _c560 = _registry.Find("SPY", expiry, OptionRight.Call, 560)!;
        _c565 = _registry.Find("SPY", expiry, OptionRight.Call, 565)!;
        _p560 = _registry.Find("SPY", expiry, OptionRight.Put, 560)!;
    }

    public async Task DisposeAsync()
    {
        await _oms.DisposeAsync();
        await _venue.DisposeAsync();
    }

    private async Task SettleAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await _oms.FlushAsync();
            await _venue.FlushAsync();
        }
    }

    private QuoteSnapshot Q(OptionContract c) => _cache.Quote(c.Id)!;

    private IReadOnlyList<OrderLeg> Vertical => [new(_c560, 1, Side.Buy), new(_c565, 1, Side.Sell)];

    private NewSpreadRequest Spread(string id, IReadOnlyList<OrderLeg> legs, Side side, decimal price, decimal qty,
        TimeInForce tif = TimeInForce.Day) => new(_client, id, legs, null, side, tif, price, qty, null, 0);

    [Fact]
    public async Task Marketable_vertical_fills_both_legs_at_the_natural_price()
    {
        var natural = Q(_c560).Ask!.Value - Q(_c565).Bid!.Value;
        _oms.Submit(Spread("V1", Vertical, Side.Buy, natural, 3));
        await SettleAsync();

        var reports = _listener.For("V1");
        reports.Select(r => r.ExecType).Should().StartWith([ExecType.PendingNew, ExecType.New]);
        var strategyFills = reports.Where(r => r.ExecType == ExecType.Trade && r.Leg is null).ToList();
        var legFills = reports.Where(r => r.Leg is not null).ToList();
        strategyFills.Sum(r => r.LastQty).Should().Be(3);
        strategyFills.Should().OnlyContain(r => r.LastPx == natural);
        legFills.Where(r => r.Leg!.Contract == _c560).Should().OnlyContain(r => r.Leg!.Side == Side.Buy && r.Leg.Price == Q(_c560).Ask);
        legFills.Where(r => r.Leg!.Contract == _c565).Sum(r => r.Leg!.Quantity).Should().Be(3);
        reports[^1].Order.Status.Should().Be(OrdStatus.Filled);
        reports[^1].Order.Display.Should().EndWith("560/565 C vertical");
    }

    [Fact]
    public async Task Selling_a_straddle_executes_both_sells_at_the_bids()
    {
        IReadOnlyList<OrderLeg> straddle = [new(_c560, 1, Side.Buy), new(_p560, 1, Side.Buy)];
        var bids = Q(_c560).Bid!.Value + Q(_p560).Bid!.Value;
        _oms.Submit(Spread("S1", straddle, Side.Sell, bids, 1));
        await SettleAsync();

        var legs = _listener.For("S1").Where(r => r.Leg is not null).ToList();
        legs.Should().HaveCount(2).And.OnlyContain(r => r.Leg!.Side == Side.Sell);
        _listener.For("S1")[^1].Order.Status.Should().Be(OrdStatus.Filled);
        _listener.For("S1")[^1].Order.Display.Should().EndWith("560 straddle");
    }

    [Fact]
    public async Task Non_marketable_spread_rests_until_the_market_moves_to_it()
    {
        var natural = Q(_c560).Ask!.Value - Q(_c565).Bid!.Value;
        _oms.Submit(Spread("R1", Vertical, Side.Buy, natural - 0.30m, 2));
        await SettleAsync();
        _listener.For("R1")[^1].Order.Status.Should().Be(OrdStatus.New);

        _venue.Shard("SPY")!.Shock(-2.5); // vertical cheapens as SPY falls
        _venue.Shard("SPY")!.Tick();
        await SettleAsync();

        _listener.For("R1")[^1].Order.CumQty.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Resting_spread_cancels_and_cannot_be_replaced()
    {
        var natural = Q(_c560).Ask!.Value - Q(_c565).Bid!.Value;
        _oms.Submit(Spread("C1", Vertical, Side.Buy, natural - 0.50m, 1));
        await SettleAsync();
        _oms.Submit(new ReplaceOrderRequest(_client, "C1-R", "C1", natural, 2, 0));
        _oms.Submit(new CancelOrderRequest(_client, "C1-C", "C1", 0));
        await SettleAsync();

        _listener.CancelRejects.Single().Text.Should().Contain("can't be replaced");
        _listener.For("C1")[^1].Order.Status.Should().Be(OrdStatus.Canceled);
    }

    [Fact]
    public async Task IOC_spread_that_cannot_trade_is_canceled()
    {
        var natural = Q(_c560).Ask!.Value - Q(_c565).Bid!.Value;
        _oms.Submit(Spread("I1", Vertical, Side.Buy, natural - 0.50m, 1, TimeInForce.ImmediateOrCancel));
        await SettleAsync();

        _listener.For("I1").Select(r => r.ExecType).Should().Equal(ExecType.PendingNew, ExecType.New, ExecType.Canceled);
    }

    [Fact]
    public async Task Spread_risk_checks()
    {
        var aapl = _registry.Chain("AAPL")[0];
        _oms.Submit(Spread("X1", [new(_c560, 1, Side.Buy), new(aapl, 1, Side.Sell)], Side.Buy, 1, 1));
        _oms.Submit(Spread("X2", Vertical, Side.Buy, 9.99m, 1));
        _oms.Submit(Spread("X3", [new(_c560, 1, Side.Buy)], Side.Buy, 1, 1));
        _oms.Submit(Spread("X4", Vertical, Side.Buy, 1, 1, TimeInForce.FillOrKill));
        await SettleAsync();

        _listener.RiskRejects.Select(r => r.Code).Should().Equal(RiskRejectCode.SymbolNotAllowed, RiskRejectCode.PriceBand,
            RiskRejectCode.InvalidQuantity, RiskRejectCode.TimeInForceNotAllowed);
        _listener.For("X2").Single().Text.Should().Contain("strategy theo");
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
