using Tickwire.Venue;

namespace Tickwire.Engine;

/// <summary>Per-client pre-trade limits. Immutable; changing limits swaps the whole record, so changes apply atomically.</summary>
public sealed record RiskLimits
{
    public decimal MaxOrderQty { get; init; } = 500;

    /// <summary>Price × quantity × 100 (contract multiplier), in dollars.</summary>
    public decimal MaxNotional { get; init; } = 250_000;

    /// <summary>Largest allowed distance of a limit price from theo, as a fraction of theo (fat-finger check).</summary>
    public decimal PriceBandPct { get; init; } = 0.50m;

    /// <summary>Minimum band width in dollars, so cheap options aren't rejected for a few cents.</summary>
    public decimal PriceBandMinAbs { get; init; } = 0.25m;

    public int MaxOpenOrders { get; init; } = 50;
    public IReadOnlyList<string>? AllowedUnderlyings { get; init; }
    public IReadOnlyList<OrderType> AllowedOrderTypes { get; init; } = [OrderType.Limit, OrderType.Market];
    public IReadOnlyList<TimeInForce> AllowedTimeInForce { get; init; } =
        [TimeInForce.Day, TimeInForce.ImmediateOrCancel, TimeInForce.FillOrKill];

    /// <summary>New/cancel/replace messages per second before throttling.</summary>
    public int MaxMessagesPerSecond { get; init; } = 20;

    /// <summary>Cancel all open orders when the client's FIX session disconnects.</summary>
    public bool CancelOnDisconnect { get; init; } = true;

    public static RiskLimits Guest => new()
    {
        MaxOrderQty = 100,
        MaxNotional = 50_000,
        MaxOpenOrders = 25,
        MaxMessagesPerSecond = 10,
    };
}

public sealed class ClientAccount
{
    private volatile RiskLimits _limits;
    private volatile bool _killSwitch;

    public ClientAccount(string clientId, string displayName, RiskLimits limits, bool isGuest = false)
    {
        ClientId = clientId;
        DisplayName = displayName;
        _limits = limits;
        IsGuest = isGuest;
    }

    public string ClientId { get; }
    public string DisplayName { get; }
    public bool IsGuest { get; }

    public RiskLimits Limits
    {
        get => _limits;
        set => _limits = value;
    }

    public bool KillSwitch
    {
        get => _killSwitch;
        set => _killSwitch = value;
    }
}

public enum RiskRejectCode
{
    KillSwitch,
    Throttle,
    DuplicateClOrdID,
    UnknownInstrument,
    SymbolNotAllowed,
    OrderTypeNotAllowed,
    TimeInForceNotAllowed,
    InvalidQuantity,
    InvalidPrice,
    MaxOrderQty,
    MaxNotional,
    PriceBand,
    MaxOpenOrders,
    NoMarketData,
}

public readonly record struct RiskResult(RiskRejectCode Code, string Text, OrdRejReason FixReason);

/// <summary>Pre-trade checks. Pure functions of the request, the limits and market data, so they are easy to test.</summary>
public static class PreTradeRisk
{
    public static RiskResult? Check(RiskLimits limits, OptionContract contract, OrderType type, TimeInForce tif, decimal? price,
        decimal quantity, int openOrders, double? theo, decimal? bestOpposite)
    {
        if (limits.AllowedUnderlyings is { Count: > 0 } allowed
            && !allowed.Contains(contract.Underlying, StringComparer.OrdinalIgnoreCase))
        {
            return new(RiskRejectCode.SymbolNotAllowed, $"Underlying {contract.Underlying} is not enabled for this account",
                OrdRejReason.BrokerOption);
        }

        if (!limits.AllowedOrderTypes.Contains(type))
        {
            return new(RiskRejectCode.OrderTypeNotAllowed, $"Order type {type} is not enabled for this account", OrdRejReason.BrokerOption);
        }

        if (!limits.AllowedTimeInForce.Contains(tif))
        {
            return new(RiskRejectCode.TimeInForceNotAllowed, $"Time in force {tif} is not enabled for this account", OrdRejReason.BrokerOption);
        }

        if (quantity <= 0 || quantity != Math.Floor(quantity))
        {
            return new(RiskRejectCode.InvalidQuantity, $"OrderQty must be a positive whole number of contracts (got {quantity})",
                OrdRejReason.Other);
        }

        if (type == OrderType.Limit)
        {
            if (price is null or <= 0)
            {
                return new(RiskRejectCode.InvalidPrice, "Limit order requires a positive Price(44)", OrdRejReason.Other);
            }

            if (price % contract.TickSize != 0)
            {
                return new(RiskRejectCode.InvalidPrice, $"Price {price} is not a multiple of the tick size {contract.TickSize}",
                    OrdRejReason.Other);
            }
        }

        if (quantity > limits.MaxOrderQty)
        {
            return new(RiskRejectCode.MaxOrderQty, $"OrderQty {quantity} exceeds max order quantity {limits.MaxOrderQty}",
                OrdRejReason.OrderExceedsLimit);
        }

        var referencePx = price ?? bestOpposite ?? (theo is { } t ? (decimal)t : null);
        if (referencePx is null)
        {
            return new(RiskRejectCode.NoMarketData, "No market data for market order notional check", OrdRejReason.Other);
        }

        var notional = referencePx.Value * quantity * OptionContract.Multiplier;
        if (notional > limits.MaxNotional)
        {
            return new(RiskRejectCode.MaxNotional, $"Notional ${notional:N0} exceeds max notional ${limits.MaxNotional:N0}",
                OrdRejReason.OrderExceedsLimit);
        }

        if (type == OrderType.Limit && theo is { } theoValue)
        {
            var theoPx = (decimal)theoValue;
            var band = Math.Max(theoPx * limits.PriceBandPct, limits.PriceBandMinAbs);
            if (Math.Abs(price!.Value - theoPx) > band)
            {
                return new(RiskRejectCode.PriceBand,
                    $"Price {price:0.00} is outside the band of theo {theoPx:0.00} ± {band:0.00} (fat-finger check)",
                    OrdRejReason.Other);
            }
        }

        if (openOrders >= limits.MaxOpenOrders)
        {
            return new(RiskRejectCode.MaxOpenOrders, $"Open order limit of {limits.MaxOpenOrders} reached", OrdRejReason.OrderExceedsLimit);
        }

        return null;
    }

    /// <summary>
    /// Checks for multi-leg orders: 2 to 4 legs on one underlying, whole ratios 1-10, limit orders only, IOC or Day.
    /// Notional is the gross theo value of all legs; the price band is measured against the strategy's theo.
    /// </summary>
    public static RiskResult? CheckSpread(RiskLimits limits, IReadOnlyList<OrderLeg> legs, TimeInForce tif, decimal? price, decimal quantity,
        int openOrders, Func<int, double?> theoOf)
    {
        if (legs.Count is < 2 or > 4)
        {
            return new(RiskRejectCode.InvalidQuantity, "Multi-leg orders need 2 to 4 legs", OrdRejReason.Other);
        }

        if (legs.Select(l => l.Contract.Underlying).Distinct().Count() > 1)
        {
            return new(RiskRejectCode.SymbolNotAllowed, "All legs must be on the same underlying", OrdRejReason.Other);
        }

        if (legs.Select(l => l.Contract.Id).Distinct().Count() != legs.Count)
        {
            return new(RiskRejectCode.InvalidQuantity, "The same contract appears in more than one leg", OrdRejReason.Other);
        }

        if (legs.Any(l => l.Ratio is < 1 or > 10))
        {
            return new(RiskRejectCode.InvalidQuantity, "Leg ratios must be whole numbers from 1 to 10", OrdRejReason.Other);
        }

        if (tif == TimeInForce.FillOrKill)
        {
            return new(RiskRejectCode.TimeInForceNotAllowed, "Fill-or-kill isn't supported for multi-leg orders; use IOC", OrdRejReason.BrokerOption);
        }

        if (price is null)
        {
            return new(RiskRejectCode.InvalidPrice, "Multi-leg orders need a net limit price (Price 44)", OrdRejReason.Other);
        }

        if (price % 0.01m != 0)
        {
            return new(RiskRejectCode.InvalidPrice, $"Net price {price} is not a multiple of 0.01", OrdRejReason.Other);
        }

        var first = Check(limits, legs[0].Contract, OrderType.Limit, tif, 0.01m, quantity, openOrders, null, null);
        if (first is { Code: not RiskRejectCode.MaxNotional and not RiskRejectCode.PriceBand } r)
        {
            return r; // allowed products, TIF, quantity and open-order checks apply to the strategy as a whole
        }

        var theos = legs.Select(l => theoOf(l.Contract.Id)).ToList();
        if (theos.Any(t => t is null))
        {
            return new(RiskRejectCode.NoMarketData, "No market data for one of the legs", OrdRejReason.Other);
        }

        var gross = legs.Zip(theos, (l, t) => (decimal)t!.Value * l.Ratio).Sum();
        var notional = gross * quantity * OptionContract.Multiplier;
        if (notional > limits.MaxNotional)
        {
            return new(RiskRejectCode.MaxNotional, $"Gross leg notional ${notional:N0} exceeds max notional ${limits.MaxNotional:N0}",
                OrdRejReason.OrderExceedsLimit);
        }

        var strategyTheo = legs.Zip(theos, (l, t) => (l.Side == Side.Buy ? 1 : -1) * l.Ratio * (decimal)t!.Value).Sum();
        var band = Math.Max(gross * limits.PriceBandPct, limits.PriceBandMinAbs);
        if (Math.Abs(price.Value - strategyTheo) > band)
        {
            return new(RiskRejectCode.PriceBand,
                $"Net price {price:0.00} is outside the band of strategy theo {strategyTheo:0.00} ± {band:0.00} (fat-finger check)",
                OrdRejReason.Other);
        }

        return null;
    }
}

/// <summary>Sliding one-second window of message timestamps per client.</summary>
public sealed class MessageThrottle
{
    private readonly Dictionary<string, Queue<long>> _windows = [];
    private readonly TimeProvider _time;

    public MessageThrottle(TimeProvider time) => _time = time;

    /// <summary>Records one message and returns false when the client is over its per-second limit.</summary>
    public bool TryAcquire(string clientId, int maxPerSecond)
    {
        var now = _time.GetTimestamp();
        var cutoff = now - _time.TimestampFrequency;
        if (!_windows.TryGetValue(clientId, out var window))
        {
            window = new Queue<long>();
            _windows[clientId] = window;
        }

        while (window.Count > 0 && window.Peek() <= cutoff)
        {
            window.Dequeue();
        }

        if (window.Count >= maxPerSecond)
        {
            return false;
        }

        window.Enqueue(now);
        return true;
    }
}
