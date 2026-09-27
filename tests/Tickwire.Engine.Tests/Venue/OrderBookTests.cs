using FluentAssertions;
using Tickwire.Pricing;
using Tickwire.Venue;

namespace Tickwire.Engine.Tests.Venue;

public class OrderBookTests
{
    private static OrderBook NewBook() => new(new OptionContract(1, "SPY", new DateOnly(2030, 1, 18), OptionRight.Call, 550));

    [Fact]
    public void Best_price_trades_first_then_time_priority()
    {
        var book = NewBook();
        book.Submit(-1, Side.Sell, OrderType.Limit, TimeInForce.Day, 2.10m, 5);
        book.Submit(-2, Side.Sell, OrderType.Limit, TimeInForce.Day, 2.00m, 5);
        book.Submit(-3, Side.Sell, OrderType.Limit, TimeInForce.Day, 2.00m, 5);

        var result = book.Submit(1, Side.Buy, OrderType.Limit, TimeInForce.Day, 2.10m, 12);

        result.Fills.Should().Equal(
            new Fill(1, -2, 2.00m, 5),
            new Fill(1, -3, 2.00m, 5),
            new Fill(1, -1, 2.10m, 2));
        result.RestingQuantity.Should().Be(0);
        book.BestAsk.Should().Be(new BookLevel(2.10m, 3, 1));
    }

    [Fact]
    public void Trades_at_the_resting_price_and_rests_the_remainder()
    {
        var book = NewBook();
        book.Submit(-1, Side.Sell, OrderType.Limit, TimeInForce.Day, 1.95m, 3);

        var result = book.Submit(1, Side.Buy, OrderType.Limit, TimeInForce.Day, 2.05m, 10);

        result.Fills.Should().ContainSingle().Which.Price.Should().Be(1.95m);
        result.RestingQuantity.Should().Be(7);
        book.BestBid.Should().Be(new BookLevel(2.05m, 7, 1));
    }

    [Fact]
    public void IOC_cancels_the_remainder()
    {
        var book = NewBook();
        book.Submit(-1, Side.Buy, OrderType.Limit, TimeInForce.Day, 1.00m, 4);

        var result = book.Submit(1, Side.Sell, OrderType.Limit, TimeInForce.ImmediateOrCancel, 1.00m, 10);

        result.FilledQuantity.Should().Be(4);
        result.CanceledQuantity.Should().Be(6);
        book.OrderCount.Should().Be(0);
    }

    [Fact]
    public void FOK_trades_all_or_nothing()
    {
        var book = NewBook();
        book.Submit(-1, Side.Sell, OrderType.Limit, TimeInForce.Day, 1.00m, 4);
        book.Submit(-2, Side.Sell, OrderType.Limit, TimeInForce.Day, 1.05m, 4);

        var tooBig = book.Submit(1, Side.Buy, OrderType.Limit, TimeInForce.FillOrKill, 1.00m, 5);
        tooBig.Fills.Should().BeEmpty();
        tooBig.CanceledQuantity.Should().Be(5);
        book.BestAsk!.Value.Quantity.Should().Be(4);

        var fits = book.Submit(2, Side.Buy, OrderType.Limit, TimeInForce.FillOrKill, 1.05m, 5);
        fits.FilledQuantity.Should().Be(5);
    }

    [Fact]
    public void Market_order_never_rests()
    {
        var book = NewBook();

        var empty = book.Submit(1, Side.Buy, OrderType.Market, TimeInForce.Day, null, 5);
        empty.CanceledQuantity.Should().Be(5);
        empty.CancelReason.Should().Contain("No liquidity");

        book.Submit(-1, Side.Sell, OrderType.Limit, TimeInForce.Day, 3.00m, 2);
        var partial = book.Submit(2, Side.Buy, OrderType.Market, TimeInForce.Day, null, 5);
        partial.FilledQuantity.Should().Be(2);
        partial.CanceledQuantity.Should().Be(3);
        book.OrderCount.Should().Be(0);
    }

    [Fact]
    public void Reducing_quantity_keeps_priority_but_changing_price_loses_it()
    {
        var book = NewBook();
        book.Submit(1, Side.Buy, OrderType.Limit, TimeInForce.Day, 1.00m, 10);
        book.Submit(2, Side.Buy, OrderType.Limit, TimeInForce.Day, 1.00m, 10);

        book.Replace(1, 1.00m, 5);
        book.Submit(-1, Side.Sell, OrderType.Limit, TimeInForce.Day, 1.00m, 5).Fills.Single().RestingId.Should().Be(1);

        book.Submit(3, Side.Buy, OrderType.Limit, TimeInForce.Day, 1.00m, 10);
        book.Replace(2, 1.00m, 20); // increase: goes to the back
        book.Submit(-2, Side.Sell, OrderType.Limit, TimeInForce.Day, 1.00m, 1).Fills.Single().RestingId.Should().Be(3);
    }

    [Fact]
    public void Replace_to_a_crossing_price_trades()
    {
        var book = NewBook();
        book.Submit(-1, Side.Sell, OrderType.Limit, TimeInForce.Day, 2.00m, 5);
        book.Submit(1, Side.Buy, OrderType.Limit, TimeInForce.Day, 1.50m, 5);

        var result = book.Replace(1, 2.00m, 5)!.Value;

        result.FilledQuantity.Should().Be(5);
        book.OrderCount.Should().Be(0);
    }

    [Fact]
    public void Cancel_returns_open_quantity()
    {
        var book = NewBook();
        book.Submit(1, Side.Buy, OrderType.Limit, TimeInForce.Day, 1.00m, 7);

        book.Cancel(1).Should().Be(7);
        book.Cancel(1).Should().BeNull();
        book.BestBid.Should().BeNull();
    }

    [Fact]
    public void Depth_aggregates_levels()
    {
        var book = NewBook();
        book.Submit(1, Side.Buy, OrderType.Limit, TimeInForce.Day, 1.00m, 7);
        book.Submit(2, Side.Buy, OrderType.Limit, TimeInForce.Day, 1.00m, 3);
        book.Submit(3, Side.Buy, OrderType.Limit, TimeInForce.Day, 0.95m, 1);

        book.Depth(Side.Buy, 5).Should().Equal(new BookLevel(1.00m, 10, 2), new BookLevel(0.95m, 1, 1));
    }
}
