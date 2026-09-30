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
        CumQty, LeavesQty, Math.Round(AvgPx, 6), Status, Account, CreatedAt, UpdatedAt, Text);
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
    string? Text);

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
    long? ReceivedTimestamp);

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
