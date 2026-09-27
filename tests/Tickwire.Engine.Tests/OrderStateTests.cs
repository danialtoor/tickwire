using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Tickwire.Pricing;
using Tickwire.Venue;

namespace Tickwire.Engine.Tests;

public class OrderStateTests
{
    private static Order NewOrder(decimal qty = 10) => new()
    {
        Id = 1,
        ClientId = "C",
        ClOrdID = "A",
        Contract = new OptionContract(1, "SPY", new DateOnly(2030, 1, 18), OptionRight.Call, 550),
        Side = Side.Buy,
        OrdType = OrderType.Limit,
        TimeInForce = TimeInForce.Day,
        OrderQty = qty,
        LeavesQty = qty,
        Price = 1,
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void Terminal_states_have_no_exits()
    {
        var terminal = new[] { OrdStatus.Filled, OrdStatus.Canceled, OrdStatus.Rejected, OrdStatus.Expired };

        OrderStateMachine.Transitions.Should().NotContain(t => terminal.Contains(t.From));
    }

    [Fact]
    public void Illegal_transition_throws()
    {
        var order = NewOrder();
        order.Apply(OrderEvent.Accept);
        order.AddFill(1, 10);

        order.Invoking(o => o.Apply(OrderEvent.Cancel)).Should().Throw<InvalidOperationException>().WithMessage("*Filled + Cancel*");
    }

    [Fact]
    public void Rejected_cancel_returns_to_working_status_based_on_fills()
    {
        var order = NewOrder();
        order.Apply(OrderEvent.Accept);
        order.AddFill(1, 3);
        order.Apply(OrderEvent.RequestCancel);
        order.Status.Should().Be(OrdStatus.PendingCancel);

        order.AddFill(1, 2);
        order.Status.Should().Be(OrdStatus.PendingCancel, "a pending cancel outranks partial fills");

        order.Apply(OrderEvent.RejectCancel);
        order.Status.Should().Be(OrdStatus.PartiallyFilled);
    }

    [Fact]
    public void Overfill_is_impossible()
    {
        var order = NewOrder(5);
        order.Apply(OrderEvent.Accept);

        order.Invoking(o => o.AddFill(1, 6)).Should().Throw<InvalidOperationException>();
    }

    [Property(MaxTest = 300)]
    public Property Fills_keep_quantity_and_average_price_invariants() =>
        Prop.ForAll(
            (from qty in Gen.Choose(1, 500)
             from fills in Gen.Choose(1, 20).ArrayOf()
             from prices in Gen.Choose(1, 2000).ArrayOf()
             select (qty, fills, prices)).ToArbitrary(),
            x =>
            {
                var order = NewOrder(x.qty);
                order.Apply(OrderEvent.Accept);
                decimal notional = 0;
                for (var i = 0; i < x.fills.Length && order.LeavesQty > 0; i++)
                {
                    var q = Math.Min(x.fills[i], order.LeavesQty);
                    var px = x.prices.Length == 0 ? 1m : x.prices[i % x.prices.Length] / 100m;
                    order.AddFill(px, q);
                    notional += px * q;
                    if (order.CumQty + order.LeavesQty != order.OrderQty)
                    {
                        return false;
                    }

                    if (Math.Abs(order.AvgPx - (notional / order.CumQty)) > 0.0000001m)
                    {
                        return false;
                    }

                    var expected = order.LeavesQty == 0 ? OrdStatus.Filled : OrdStatus.PartiallyFilled;
                    if (order.Status != expected)
                    {
                        return false;
                    }
                }

                return true;
            });
}
