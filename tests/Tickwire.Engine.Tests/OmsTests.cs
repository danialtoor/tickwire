using System.Collections.Concurrent;
using FluentAssertions;
using Tickwire.Pricing;
using Tickwire.Venue;

namespace Tickwire.Engine.Tests;

internal sealed class RecordingListener : IOmsListener
{
    public ConcurrentQueue<ExecutionReportEvent> Reports { get; } = new();
    public ConcurrentQueue<CancelRejectEvent> CancelRejects { get; } = new();
    public ConcurrentQueue<(string Client, RiskRejectCode Code)> RiskRejects { get; } = new();

    public void OnExecutionReport(ExecutionReportEvent report) => Reports.Enqueue(report);

    public void OnCancelReject(CancelRejectEvent reject) => CancelRejects.Enqueue(reject);

    public void OnRiskReject(string clientId, RiskRejectCode code, string text) => RiskRejects.Enqueue((clientId, code));

    public IReadOnlyList<ExecutionReportEvent> For(string clOrdIdOrOrig) =>
        [.. Reports.Where(r => r.Order.ClOrdID == clOrdIdOrOrig || r.Order.OrigClOrdID == clOrdIdOrOrig)];
}

/// <summary>OMS against the real simulated venue: market makers on, noise traders and timers off, so results are deterministic.</summary>
public sealed class OmsTests : IAsyncLifetime
{
    private readonly MarketDataCache _cache = new();
    private readonly RecordingListener _listener = new();
    private SimulatedVenue _venue = null!;
    private OrderManager _oms = null!;
    private readonly ClientAccount _alice = new("alice", "Alice", new RiskLimits());
    private readonly ClientAccount _bob = new("bob", "Bob", new RiskLimits());
    private OptionContract _atmCall = null!;

    public async Task InitializeAsync()
    {
        var registry = InstrumentRegistry.Build(new DateOnly(2025, 3, 10));
        _venue = new SimulatedVenue(registry, new VenueOptions { EnableNoiseTraders = false }, _cache,
            new FixedTime(new DateTime(2025, 3, 10, 15, 0, 0, DateTimeKind.Utc)));
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

    private NewOrderRequest Order(ClientAccount who, string clOrdId, Side side, decimal? price, decimal qty,
        TimeInForce tif = TimeInForce.Day, OrderType type = OrderType.Limit, OptionContract? contract = null) =>
        new(who, clOrdId, contract ?? _atmCall, null, side, type, tif, price, qty, null, 0);

    [Fact]
    public void Market_makers_quote_around_theo()
    {
        Quote.Bid.Should().BeLessThan((decimal)Quote.Theo);
        Quote.Ask.Should().BeGreaterThan((decimal)Quote.Theo);
        Quote.Bids.Should().HaveCount(2, "two market makers quote at different widths");
    }

    [Fact]
    public async Task Marketable_order_fills_across_levels_with_correct_lifecycle()
    {
        var qty = Quote.AskSize + 5; // more than the best level, so it sweeps into the second maker
        _oms.Submit(Order(_alice, "A1", Side.Buy, Quote.Asks[1].Price, qty));
        await SettleAsync();

        var reports = _listener.For("A1");
        reports.Select(r => r.ExecType).Should().StartWith([ExecType.PendingNew, ExecType.New]);
        reports.Where(r => r.ExecType == ExecType.Trade).Should().HaveCountGreaterThanOrEqualTo(2);
        reports[^1].Order.Status.Should().Be(OrdStatus.Filled);
        reports.Should().OnlyContain(r => r.Order.CumQty + r.Order.LeavesQty == r.Order.OrderQty);
        var trades = reports.Where(r => r.ExecType == ExecType.Trade).ToList();
        reports[^1].Order.AvgPx.Should().Be(Math.Round(trades.Sum(t => t.LastPx * t.LastQty) / trades.Sum(t => t.LastQty), 6));
    }

    [Fact]
    public async Task Resting_order_cancel_uses_ClOrdID_chain()
    {
        _oms.Submit(Order(_alice, "R1", Side.Buy, Quote.Bid!.Value - 0.05m, 3));
        await SettleAsync();
        _oms.Submit(new CancelOrderRequest(_alice, "R1-C", "R1", 0));
        await SettleAsync();

        var reports = _listener.For("R1");
        reports.Select(r => r.ExecType).Should().Equal(ExecType.PendingNew, ExecType.New, ExecType.PendingCancel, ExecType.Canceled);
        var canceled = reports[^1].Order;
        canceled.ClOrdID.Should().Be("R1-C");
        canceled.OrigClOrdID.Should().Be("R1");
        canceled.LeavesQty.Should().Be(0);
        canceled.Status.Should().Be(OrdStatus.Canceled);
    }

    [Fact]
    public async Task Replace_updates_price_quantity_and_ClOrdID()
    {
        var px = Quote.Bid!.Value - 0.10m;
        _oms.Submit(Order(_alice, "P1", Side.Buy, px, 3));
        await SettleAsync();
        _oms.Submit(new ReplaceOrderRequest(_alice, "P2", "P1", px + 0.01m, 6, 0));
        await SettleAsync();

        var replaced = _listener.Reports.Single(r => r.ExecType == ExecType.Replaced).Order;
        replaced.ClOrdID.Should().Be("P2");
        replaced.OrigClOrdID.Should().Be("P1");
        replaced.OrderQty.Should().Be(6);
        replaced.Price.Should().Be(px + 0.01m);
        replaced.LeavesQty.Should().Be(6);
        replaced.Status.Should().Be(OrdStatus.New);

        // A cancel that still points at the old ClOrdID is rejected.
        _oms.Submit(new CancelOrderRequest(_alice, "P3", "P1", 0));
        await SettleAsync();
        _listener.CancelRejects.Single().Text.Should().Contain("stale");
    }

    [Fact]
    public async Task Duplicate_ClOrdID_and_unknown_instrument_are_rejected()
    {
        _oms.Submit(Order(_alice, "D1", Side.Buy, Quote.Bid!.Value - 0.05m, 1));
        _oms.Submit(Order(_alice, "D1", Side.Buy, Quote.Bid!.Value - 0.05m, 1));
        _oms.Submit(new NewOrderRequest(_alice, "U1", null, "Unknown symbol 'XYZ'", Side.Buy, OrderType.Limit, TimeInForce.Day, 1, 1,
            null, 0));
        await SettleAsync();

        var rejects = _listener.Reports.Where(r => r.ExecType == ExecType.Rejected).ToList();
        rejects.Should().HaveCount(2);
        rejects[0].RejectReason.Should().Be(OrdRejReason.DuplicateOrder);
        rejects[1].RejectReason.Should().Be(OrdRejReason.UnknownSymbol);
        rejects[1].Text.Should().Contain("XYZ");
        _listener.For("D1").Count(r => r.ExecType == ExecType.New).Should().Be(1);
    }

    [Fact]
    public async Task Risk_checks_reject_with_clear_reasons()
    {
        var theo = (decimal)Quote.Theo;
        _oms.Submit(Order(_alice, "FAT", Side.Buy, Math.Round(theo * 3, 2), 1));
        _oms.Submit(Order(_alice, "BIG", Side.Buy, Quote.Bid!.Value, 10_000));
        _oms.Submit(Order(_alice, "TICK", Side.Buy, Quote.Bid!.Value + 0.005m, 1));
        await SettleAsync();

        _listener.RiskRejects.Select(r => r.Code).Should().Equal(RiskRejectCode.PriceBand, RiskRejectCode.MaxOrderQty, RiskRejectCode.InvalidPrice);
        _listener.For("FAT").Single().Text.Should().Contain("fat-finger");
    }

    [Fact]
    public async Task Limits_changed_at_runtime_apply_to_the_next_order()
    {
        _alice.Limits = _alice.Limits with { AllowedUnderlyings = ["AAPL"] };
        _oms.Submit(Order(_alice, "L1", Side.Buy, Quote.Bid!.Value, 1));
        await SettleAsync();

        _listener.RiskRejects.Single().Code.Should().Be(RiskRejectCode.SymbolNotAllowed);
    }

    [Fact]
    public async Task Kill_switch_cancels_open_orders_and_blocks_new_ones()
    {
        _oms.Submit(Order(_alice, "K1", Side.Buy, Quote.Bid!.Value - 0.05m, 1));
        _oms.Submit(Order(_alice, "K2", Side.Sell, Quote.Ask!.Value + 0.05m, 1));
        _oms.Submit(Order(_bob, "B1", Side.Buy, Quote.Bid!.Value - 0.05m, 1));
        await SettleAsync();

        _alice.KillSwitch = true;
        (await _oms.KillSwitchAsync("alice", true, "Kill switch: risk officer")).Should().Be(2);
        _oms.Submit(Order(_alice, "K3", Side.Buy, Quote.Bid!.Value, 1));
        await SettleAsync();

        _listener.For("K1")[^1].Order.Status.Should().Be(OrdStatus.Canceled);
        _listener.For("K2")[^1].Text.Should().Be("Kill switch: risk officer");
        _listener.For("K3").Single().ExecType.Should().Be(ExecType.Rejected);
        _listener.For("B1")[^1].Order.Status.Should().Be(OrdStatus.New, "other clients are unaffected");
    }

    [Fact]
    public async Task Two_clients_trade_with_each_other()
    {
        var px = Quote.Bid!.Value + 0.01m; // inside the makers' spread
        _oms.Submit(Order(_alice, "S1", Side.Sell, px, 4));
        await SettleAsync();
        _oms.Submit(Order(_bob, "B1", Side.Buy, px, 4));
        await SettleAsync();

        _listener.For("S1")[^1].Order.Status.Should().Be(OrdStatus.Filled);
        _listener.For("B1")[^1].Order.Status.Should().Be(OrdStatus.Filled);
        _listener.For("B1").Single(r => r.ExecType == ExecType.Trade).LastPx.Should().Be(px);
    }

    [Fact]
    public async Task IOC_partial_fill_then_cancel()
    {
        _oms.Submit(Order(_alice, "I1", Side.Buy, Quote.Ask!.Value, Quote.AskSize + 50, TimeInForce.ImmediateOrCancel));
        await SettleAsync();

        var reports = _listener.For("I1");
        reports.Select(r => r.ExecType).Should().StartWith([ExecType.PendingNew, ExecType.New, ExecType.Trade]).And.EndWith(ExecType.Canceled);
        reports[^1].Order.CumQty.Should().BeGreaterThan(0);
        reports[^1].Order.LeavesQty.Should().Be(0);
    }

    [Fact]
    public async Task Cancel_of_unknown_or_filled_order_is_rejected()
    {
        _oms.Submit(new CancelOrderRequest(_alice, "X-C", "NOPE", 0));
        _oms.Submit(Order(_alice, "F1", Side.Buy, Quote.Ask!.Value, 1));
        await SettleAsync();
        _oms.Submit(new CancelOrderRequest(_alice, "F1-C", "F1", 0));
        await SettleAsync();

        _listener.CancelRejects.Select(r => r.Reason).Should().Equal(CxlRejReason.UnknownOrder, CxlRejReason.TooLateToCancel);
    }

    [Fact]
    public async Task Throttle_limits_messages_per_second()
    {
        var client = new ClientAccount("fast", "Fast", new RiskLimits { MaxMessagesPerSecond = 3 });
        for (var i = 0; i < 5; i++)
        {
            _oms.Submit(Order(client, $"T{i}", Side.Buy, Quote.Bid!.Value - 0.10m, 1));
        }

        await SettleAsync();
        _listener.RiskRejects.Count(r => r.Code == RiskRejectCode.Throttle).Should().Be(2);
    }

    [Fact]
    public async Task Resting_order_fills_when_the_market_moves_through_it()
    {
        var px = Quote.Bid!.Value + 0.01m;
        _oms.Submit(Order(_alice, "M1", Side.Buy, px, 2));
        await SettleAsync();

        _venue.Shard("SPY")!.Shock(-3); // underlying drops, call theo drops, makers' new asks cross the resting bid
        await SettleAsync();

        _listener.For("M1")[^1].Order.Status.Should().Be(OrdStatus.Filled);
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
