using FluentAssertions;
using Tickwire.Pricing;
using Tickwire.Venue;

namespace Tickwire.Engine.Tests;

public class PositionTests
{
    private static readonly OptionContract Call = new(1, "SPY", new DateOnly(2030, 1, 18), OptionRight.Call, 560);
    private static readonly OptionContract Put = new(2, "SPY", new DateOnly(2030, 1, 18), OptionRight.Put, 560);

    private static MarketDataCache CacheWith(decimal bid, decimal ask, double delta = 0.5, double vega = 0.3)
    {
        var cache = new MarketDataCache();
        foreach (var c in new[] { Call, Put })
        {
            cache.Publish(new QuoteSnapshot(c.Id, (double)((bid + ask) / 2), 0.15, c == Call ? delta : -delta, 0.02, vega, -0.05, bid, 10, ask,
                10, null, 0, [], [], DateTime.UtcNow));
        }

        return cache;
    }

    [Fact]
    public void Average_cost_realized_and_unrealized_pnl()
    {
        var keeper = new PositionKeeper(CacheWith(4.90m, 5.10m));
        keeper.Apply("a", Call, Side.Buy, 10, 4.00m);
        keeper.Apply("a", Call, Side.Buy, 10, 5.00m); // avg 4.50
        keeper.Apply("a", Call, Side.Sell, 5, 6.00m); // realize (6 - 4.5) x 5 x 100 = 750

        var p = keeper.Snapshot("a");
        var pos = p.Positions.Single();
        pos.Quantity.Should().Be(15);
        pos.AvgCost.Should().Be(4.50m);
        pos.RealizedPnl.Should().Be(750m);
        pos.Mark.Should().Be(5.00m);
        pos.UnrealizedPnl.Should().Be(750m); // (5.00 - 4.50) x 15 x 100
        p.TotalPnl.Should().Be(1500m);
        pos.Delta.Should().Be(750); // 0.5 x 15 x 100 shares
    }

    [Fact]
    public void Flipping_through_zero_restarts_the_average()
    {
        var keeper = new PositionKeeper(CacheWith(1, 1.2m));
        keeper.Apply("a", Call, Side.Buy, 2, 3.00m);
        keeper.Apply("a", Call, Side.Sell, 5, 3.50m);

        var pos = keeper.Snapshot("a").Positions.Single();
        pos.Quantity.Should().Be(-3);
        pos.AvgCost.Should().Be(3.50m);
        pos.RealizedPnl.Should().Be(100m); // (3.50 - 3.00) x 2 x 100
    }

    [Fact]
    public void Expiry_settles_at_intrinsic_into_realized_pnl()
    {
        var keeper = new PositionKeeper(CacheWith(1, 1.2m));
        keeper.Apply("a", Call, Side.Buy, 2, 4.00m);
        keeper.Apply("a", Put, Side.Sell, 1, 3.00m);

        keeper.Settle(Call.Expiry, new Dictionary<string, double> { ["SPY"] = 566.5 }).Should().Be(2);

        var p = keeper.Snapshot("a");
        p.Positions.Should().BeEmpty();
        // Call: (6.50 - 4.00) x 2 x 100 = 500. Short put expires worthless: (3.00 - 0) x 1 x 100 = 300.
        p.RealizedPnl.Should().Be(800m);
    }

    [Fact]
    public void Spread_leg_fills_become_positions_but_strategy_reports_do_not()
    {
        var keeper = new PositionKeeper(CacheWith(1, 1.2m));
        var view = new OrderView(9, "TW9", "a", "S", null, Call, Side.Buy, OrderType.Limit, TimeInForce.Day, 1, 1, 1, 0, 1, OrdStatus.Filled,
            null, DateTime.UtcNow, DateTime.UtcNow, null) { Legs = [new(Call, 1, Side.Buy), new(Put, 1, Side.Buy)] };
        keeper.OnExecutionReport(new ExecutionReportEvent(view, ExecType.Trade, "E1", 1, 2.10m, null, null, DateTime.UtcNow, null));
        keeper.OnExecutionReport(new ExecutionReportEvent(view, ExecType.Trade, "E2", 1, 1.00m, null, null, DateTime.UtcNow, null)
        {
            Leg = new LegExecution(Call, Side.Buy, 1, 1.00m),
        });
        keeper.OnExecutionReport(new ExecutionReportEvent(view, ExecType.Trade, "E3", 1, 1.10m, null, null, DateTime.UtcNow, null)
        {
            Leg = new LegExecution(Put, Side.Buy, 1, 1.10m),
        });

        keeper.Snapshot("a").Positions.Select(p => (p.Contract.Id, p.Quantity)).Should().BeEquivalentTo([(1, 1m), (2, 1m)]);
    }

    [Fact]
    public void Delta_limit_blocks_orders_that_add_exposure_but_allows_reducing_ones()
    {
        var limits = new RiskLimits { MaxAbsDelta = 1_000 };

        PortfolioLimits.Check(limits, (900, 0), (500, 0))!.Value.Code.Should().Be(RiskRejectCode.DeltaLimit);
        PortfolioLimits.Check(limits, (900, 0), (-500, 0)).Should().BeNull();
        PortfolioLimits.Check(limits, (1_500, 0), (-200, 0)).Should().BeNull("already over, but this order reduces it");
        PortfolioLimits.Check(limits with { MaxAbsVega = 100 }, (0, 90), (0, 20))!.Value.Code.Should().Be(RiskRejectCode.VegaLimit);
    }
}
