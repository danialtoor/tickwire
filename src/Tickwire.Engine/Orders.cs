using System.Collections.Frozen;
using Tickwire.Venue;

namespace Tickwire.Engine;

/// <summary>OrdStatus(39). Values are the FIX characters.</summary>
public enum OrdStatus
{
    New = '0',
    PartiallyFilled = '1',
    Filled = '2',
    Canceled = '4',
    PendingCancel = '6',
    Rejected = '8',
    PendingNew = 'A',
    Expired = 'C',
    PendingReplace = 'E',
}

/// <summary>ExecType(150). Values are the FIX characters.</summary>
public enum ExecType
{
    New = '0',
    Canceled = '4',
    Replaced = '5',
    PendingCancel = '6',
    Rejected = '8',
    PendingNew = 'A',
    Expired = 'C',
    PendingReplace = 'E',
    Trade = 'F',
    OrderStatus = 'I',
}

public enum OrderEvent
{
    Accept,
    PartialFill,
    Fill,
    RequestCancel,
    Cancel,
    RejectCancel,
    RequestReplace,
    Replace,
    RejectReplace,
    Reject,
    Expire,
}

/// <summary>
/// Every legal order state change, as an explicit table. Anything not listed is a bug and throws.
/// A null target means "back to working": New if nothing has filled, PartiallyFilled otherwise.
/// </summary>
public static class OrderStateMachine
{
    private static readonly FrozenDictionary<(OrdStatus, OrderEvent), OrdStatus?> Table = new Dictionary<(OrdStatus, OrderEvent), OrdStatus?>
    {
        [(OrdStatus.PendingNew, OrderEvent.Accept)] = OrdStatus.New,
        [(OrdStatus.PendingNew, OrderEvent.Reject)] = OrdStatus.Rejected,

        [(OrdStatus.New, OrderEvent.PartialFill)] = OrdStatus.PartiallyFilled,
        [(OrdStatus.New, OrderEvent.Fill)] = OrdStatus.Filled,
        [(OrdStatus.New, OrderEvent.RequestCancel)] = OrdStatus.PendingCancel,
        [(OrdStatus.New, OrderEvent.RequestReplace)] = OrdStatus.PendingReplace,
        [(OrdStatus.New, OrderEvent.Cancel)] = OrdStatus.Canceled,
        [(OrdStatus.New, OrderEvent.Expire)] = OrdStatus.Expired,

        [(OrdStatus.PartiallyFilled, OrderEvent.PartialFill)] = OrdStatus.PartiallyFilled,
        [(OrdStatus.PartiallyFilled, OrderEvent.Fill)] = OrdStatus.Filled,
        [(OrdStatus.PartiallyFilled, OrderEvent.RequestCancel)] = OrdStatus.PendingCancel,
        [(OrdStatus.PartiallyFilled, OrderEvent.RequestReplace)] = OrdStatus.PendingReplace,
        [(OrdStatus.PartiallyFilled, OrderEvent.Cancel)] = OrdStatus.Canceled,
        [(OrdStatus.PartiallyFilled, OrderEvent.Expire)] = OrdStatus.Expired,

        // Fills can still arrive while a cancel or replace is in flight; the pending status wins until it resolves.
        [(OrdStatus.PendingCancel, OrderEvent.PartialFill)] = OrdStatus.PendingCancel,
        [(OrdStatus.PendingCancel, OrderEvent.Fill)] = OrdStatus.Filled,
        [(OrdStatus.PendingCancel, OrderEvent.Cancel)] = OrdStatus.Canceled,
        [(OrdStatus.PendingCancel, OrderEvent.RejectCancel)] = null,
        [(OrdStatus.PendingCancel, OrderEvent.Expire)] = OrdStatus.Expired,

        [(OrdStatus.PendingReplace, OrderEvent.PartialFill)] = OrdStatus.PendingReplace,
        [(OrdStatus.PendingReplace, OrderEvent.Fill)] = OrdStatus.Filled,
        [(OrdStatus.PendingReplace, OrderEvent.Replace)] = null,
        [(OrdStatus.PendingReplace, OrderEvent.RejectReplace)] = null,
        [(OrdStatus.PendingReplace, OrderEvent.Cancel)] = OrdStatus.Canceled,
        [(OrdStatus.PendingReplace, OrderEvent.Expire)] = OrdStatus.Expired,
    }.ToFrozenDictionary();

    public static bool IsTerminal(OrdStatus status) =>
        status is OrdStatus.Filled or OrdStatus.Canceled or OrdStatus.Rejected or OrdStatus.Expired;

    public static bool CanApply(OrdStatus from, OrderEvent ev) => Table.ContainsKey((from, ev));

    public static OrdStatus Next(OrdStatus from, OrderEvent ev, decimal cumQty) =>
        Table.TryGetValue((from, ev), out var to)
            ? to ?? (cumQty > 0 ? OrdStatus.PartiallyFilled : OrdStatus.New)
            : throw new InvalidOperationException($"Illegal order transition: {from} + {ev}");

    public static IEnumerable<(OrdStatus From, OrderEvent Event, OrdStatus? To)> Transitions =>
        Table.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value));
}

/// <summary>A client order owned by the OMS loop. Only the OMS mutates it; everyone else sees <see cref="OrderView"/> snapshots.</summary>
public sealed class Order
{
    public required long Id { get; init; }
    public string OrderId => $"TW{Id:D8}";
    public required string ClientId { get; init; }
    public required OptionContract Contract { get; init; }
    public required Side Side { get; init; }
    public required OrderType OrdType { get; init; }
    public required TimeInForce TimeInForce { get; init; }
    public string? Account { get; init; }
    public required DateTime CreatedAt { get; init; }

    /// <summary>Legs of a multi-leg order; null for a single-contract order. <see cref="Contract"/> is then the first leg.</summary>
    public IReadOnlyList<OrderLeg>? Legs { get; init; }

    /// <summary>ExDestination(100) the client asked for; null means smart routing.</summary>
    public string? Destination { get; init; }

    /// <summary>The exchange the order went to (known once the venue accepts it).</summary>
    public string? Exchange { get; set; }

    /// <summary>Accumulated exchange fees on this order's fills, in dollars (negative is a net rebate).</summary>
    public decimal Fees { get; set; }

    public required string ClOrdID { get; set; }
    public string? OrigClOrdID { get; set; }
    public decimal? Price { get; set; }
    public decimal OrderQty { get; set; }
    public decimal CumQty { get; private set; }
    public decimal LeavesQty { get; set; }
    public decimal AvgPx { get; private set; }
    public OrdStatus Status { get; private set; } = OrdStatus.PendingNew;
    public DateTime UpdatedAt { get; set; }
    public string? Text { get; set; }

    /// <summary>The cancel or replace request currently in flight, if any.</summary>
    public PendingChange? Pending { get; set; }

    public bool IsTerminal => OrderStateMachine.IsTerminal(Status);
    public bool IsOpen => !IsTerminal;

    public void Apply(OrderEvent ev) => Status = OrderStateMachine.Next(Status, ev, CumQty);

    /// <summary>Records an execution. Keeps CumQty + LeavesQty = OrderQty and AvgPx volume-weighted.</summary>
    public OrderEvent AddFill(decimal price, decimal quantity)
    {
        if (quantity <= 0 || quantity > LeavesQty)
        {
            throw new InvalidOperationException($"Fill of {quantity} on {OrderId} exceeds leaves {LeavesQty}");
        }

        AvgPx = ((AvgPx * CumQty) + (price * quantity)) / (CumQty + quantity);
        CumQty += quantity;
        LeavesQty -= quantity;
        var ev = LeavesQty == 0 ? OrderEvent.Fill : OrderEvent.PartialFill;
        Apply(ev);
        return ev;
    }

    public OrderView View() => new(Id, OrderId, ClientId, ClOrdID, OrigClOrdID, Contract, Side, OrdType, TimeInForce, Price, OrderQty,
        CumQty, LeavesQty, Math.Round(AvgPx, 6), Status, Account, CreatedAt, UpdatedAt, Text)
    {
        Legs = Legs,
        Destination = Destination,
        Exchange = Exchange,
        Fees = Fees,
    };
}

public sealed record PendingChange(bool IsCancel, string ClOrdID, string OrigClOrdID, decimal? NewPrice, decimal NewQty);

/// <summary>Immutable snapshot of an order, safe to hand to other threads.</summary>
public sealed record OrderView(
    long Id,
    string OrderId,
    string ClientId,
    string ClOrdID,
    string? OrigClOrdID,
    OptionContract Contract,
    Side Side,
    OrderType OrdType,
    TimeInForce TimeInForce,
    decimal? Price,
    decimal OrderQty,
    decimal CumQty,
    decimal LeavesQty,
    decimal AvgPx,
    OrdStatus Status,
    string? Account,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? Text)
{
    public IReadOnlyList<OrderLeg>? Legs { get; init; }

    public string? Destination { get; init; }
    public string? Exchange { get; init; }
    public decimal Fees { get; init; }

    public bool IsMultileg => Legs is { Count: > 0 };

    /// <summary>"SPY 02 Oct 26 560/565 C vertical" for spreads, the contract otherwise.</summary>
    public string Display => IsMultileg ? Strategy.Describe(Legs!) : Contract.Display;
}

/// <summary>One leg of a multi-leg order, with its side when the strategy is bought.</summary>
public sealed record OrderLeg(OptionContract Contract, int Ratio, Side Side);

public static class Strategy
{
    /// <summary>Names the common two- and three-leg shapes; anything else is listed leg by leg.</summary>
    public static string Describe(IReadOnlyList<OrderLeg> legs)
    {
        var c = legs[0].Contract;
        var prefix = $"{c.Underlying} {c.Expiry:dd MMM yy}";
        string R(OptionContract x) => x.Right == Pricing.OptionRight.Call ? "C" : "P";
        var sameExpiry = legs.All(l => l.Contract.Expiry == c.Expiry);
        if (legs.Count == 2 && sameExpiry)
        {
            var (a, b) = (legs[0], legs[1]);
            if (a.Contract.Right == b.Contract.Right && a.Side != b.Side && a.Ratio == b.Ratio)
            {
                return $"{prefix} {a.Contract.Strike:0.##}/{b.Contract.Strike:0.##} {R(a.Contract)} vertical";
            }

            if (a.Contract.Right != b.Contract.Right && a.Side == b.Side && a.Ratio == b.Ratio)
            {
                return a.Contract.Strike == b.Contract.Strike
                    ? $"{prefix} {a.Contract.Strike:0.##} straddle"
                    : $"{prefix} {Math.Min(a.Contract.Strike, b.Contract.Strike):0.##}/{Math.Max(a.Contract.Strike, b.Contract.Strike):0.##} strangle";
            }
        }

        if (legs.Count == 3 && sameExpiry && legs.All(l => l.Contract.Right == c.Right) && legs[1].Ratio == 2 * legs[0].Ratio
            && legs[2].Ratio == legs[0].Ratio && legs[1].Side != legs[0].Side)
        {
            return $"{prefix} {legs[0].Contract.Strike:0.##}/{legs[1].Contract.Strike:0.##}/{legs[2].Contract.Strike:0.##} {R(c)} butterfly";
        }

        return string.Join(" + ", legs.Select(l =>
            $"{(l.Side == Side.Buy ? "+" : "-")}{l.Ratio} {l.Contract.Underlying} {l.Contract.Strike:0.##}{R(l.Contract)} {l.Contract.Expiry:dd MMM}"));
    }
}

/// <summary>OrdRejReason(103) values the engine uses.</summary>
public enum OrdRejReason
{
    BrokerOption = 0,
    UnknownSymbol = 1,
    ExchangeClosed = 2,
    OrderExceedsLimit = 3,
    TooLateToEnter = 4,
    UnknownOrder = 5,
    DuplicateOrder = 6,
    Other = 99,
}

/// <summary>CxlRejReason(102) values.</summary>
public enum CxlRejReason
{
    TooLateToCancel = 0,
    UnknownOrder = 1,
    BrokerOption = 2,
    AlreadyPending = 3,
    DuplicateClOrdID = 6,
    Other = 99,
}

/// <summary>An execution report the OMS wants delivered to the client.</summary>
public sealed record ExecutionReportEvent(
    OrderView Order,
    ExecType ExecType,
    string ExecId,
    decimal LastQty,
    decimal LastPx,
    string? Text,
    OrdRejReason? RejectReason,
    DateTime TransactTime,
    long? ReceivedTimestamp)
{
    /// <summary>For multi-leg orders: set on per-leg fill reports (MultiLegReportingType=2).</summary>
    public LegExecution? Leg { get; init; }

    /// <summary>On fills: the exchange (LastMkt 30), whether it added or removed liquidity (851), and its fee (Commission 12).</summary>
    public string? LastMkt { get; init; }

    public Liquidity? LastLiquidity { get; init; }
    public decimal? Commission { get; init; }

    /// <summary>On the New report of a routed order: why it went where it did.</summary>
    public string? RouteReason { get; init; }
}

public sealed record LegExecution(OptionContract Contract, Side Side, decimal Quantity, decimal Price);

public sealed record CancelRejectEvent(
    string ClientId,
    string ClOrdID,
    string OrigClOrdID,
    string? OrderId,
    OrdStatus OrdStatus,
    CxlRejReason Reason,
    bool ForReplace,
    string Text,
    DateTime TransactTime);
