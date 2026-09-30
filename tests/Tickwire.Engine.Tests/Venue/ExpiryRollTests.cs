using System.Collections.Concurrent;
using FluentAssertions;
using Tickwire.Pricing;
using Tickwire.Venue;

namespace Tickwire.Engine.Tests.Venue;

public class ExpiryRollTests
{
    [Fact]
    public void Mean_reversion_keeps_the_price_near_its_anchor()
    {
        // 200 paths over two simulated years (about five weeks of the demo at 20x), daily steps, TSLA-like vol.
        var random = new Random(7);
        const double dt = 1.0 / 252;
        double revertingDev = 0, walkDev = 0;
        for (var path = 0; path < 200; path++)
        {
            double spot = 250, walk = 250;
            for (var day = 0; day < 504; day++)
            {
                var z = Gbm.NextGaussian(random);
                spot = Gbm.MeanRevertingStep(spot, 250, 12, 0.55, dt, z);
                walk = Gbm.Step(walk, 0, 0.55, dt, z);
            }

            revertingDev += Math.Abs(Math.Log(spot / 250)) / 200;
            walkDev += Math.Abs(Math.Log(walk / 250)) / 200;
        }

        // Stationary spread is sigma / sqrt(2 kappa) = 0.11; mean |dev| of a normal is 0.8 sigma, so about 0.09.
        revertingDev.Should().BeInRange(0.05, 0.14);
        walkDev.Should().BeGreaterThan(0.45, "a pure random walk spreads out with sqrt(t): 0.55 x sqrt(2) x 0.8");
    }

    [Fact]
    public void Registry_lists_and_delists_expiries_with_fresh_ids()
    {
        var registry = InstrumentRegistry.Build(new DateOnly(2026, 9, 28));
        var first = registry.Expiries[0];
        var maxId = registry.All.Max(c => c.Id);

        registry.Delist(first).Should().HaveCount(4 * 17 * 2);
        var added = registry.ListExpiry(new DateOnly(2026, 11, 6), new Dictionary<string, double> { ["SPY"] = 603.4 });

        registry.Expiries.Should().NotContain(first).And.Contain(new DateOnly(2026, 11, 6)).And.HaveCount(4);
        added.Should().OnlyContain(c => c.Id > maxId);
        added.Where(c => c.Underlying == "SPY").Select(c => c.Strike).Should().Contain(605m).And.Contain(565m).And.Contain(645m);
        registry.ListExpiry(new DateOnly(2026, 11, 6), new Dictionary<string, double>()).Should().BeEmpty("already listed");
    }

    [Fact]
    public async Task Rolling_past_an_expiry_expires_resting_orders_and_lists_a_new_friday()
    {
        var cache = new MarketDataCache();
        var registry = InstrumentRegistry.Build(new DateOnly(2026, 9, 28));
        var time = new FixedTime(new DateTime(2026, 9, 28, 15, 0, 0, DateTimeKind.Utc));
        await using var venue = new SimulatedVenue(registry, new VenueOptions { EnableNoiseTraders = false }, cache, time);
        await using var oms = new OrderManager(new SimulatedVenueAdapter(venue), cache);
        var reports = new ConcurrentQueue<ExecutionReportEvent>();
        oms.AddListener(new Listener(reports));
        venue.SetSink(oms);
        venue.Start(withTimers: false);
        oms.Start();
        await venue.FlushAsync();

        var expiring = registry.Expiries[0];
        var call = registry.Chain("SPY").First(c => c.Expiry == expiring && c.Right == OptionRight.Call && c.Strike == 560);
        var bid = cache.Quote(call.Id)!.Bid!.Value;
        oms.Submit(new NewOrderRequest(new ClientAccount("c", "C", new RiskLimits()), "E1", call, null, Side.Buy, OrderType.Limit,
            TimeInForce.Day, bid - 0.10m, 2, null, 0));
        await Settle(oms, venue);

        var result = new ExpiryRoller(registry, venue, cache).Roll(InstrumentRegistry.CloseOf(expiring).AddMinutes(1));
        await Settle(oms, venue);

        result.Delisted.Should().Equal(expiring);
        result.Listed.Should().ContainSingle().Which.DayOfWeek.Should().Be(DayOfWeek.Friday);
        registry.Expiries.Should().HaveCount(4);
        var last = reports.Last(r => r.Order.ClOrdID == "E1");
        last.ExecType.Should().Be(ExecType.Expired);
        last.Order.Status.Should().Be(OrdStatus.Expired);
        last.Order.LeavesQty.Should().Be(0);
        cache.Quote(call.Id).Should().BeNull("delisted contracts drop out of market data");
        var listed = registry.Chain("SPY").First(c => c.Expiry == result.Listed[0]);
        cache.Quote(listed.Id).Should().NotBeNull("makers quote the new expiry straight away");
    }

    private static async Task Settle(OrderManager oms, SimulatedVenue venue)
    {
        for (var i = 0; i < 4; i++)
        {
            await oms.FlushAsync();
            await venue.FlushAsync();
        }
    }

    private sealed class Listener(ConcurrentQueue<ExecutionReportEvent> reports) : IOmsListener
    {
        public void OnExecutionReport(ExecutionReportEvent report) => reports.Enqueue(report);

        public void OnCancelReject(CancelRejectEvent reject)
        {
        }
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
