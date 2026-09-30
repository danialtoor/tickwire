using Tickwire.Pricing;
using Tickwire.Venue;

namespace Tickwire.Engine;

/// <summary>
/// One contract position. Greeks are in position units: delta in shares, gamma in shares per $1, vega in $ per vol
/// point, theta in $ per day.
/// </summary>
public sealed record PositionView(
    OptionContract Contract,
    decimal Quantity,
    decimal AvgCost,
    decimal Mark,
    decimal MarketValue,
    decimal UnrealizedPnl,
    decimal RealizedPnl,
    double Delta,
    double Gamma,
    double Vega,
    double Theta);

public sealed record PortfolioView(
    string ClientId,
    IReadOnlyList<PositionView> Positions,
    decimal RealizedPnl,
    decimal UnrealizedPnl,
    decimal TotalPnl,
    double Delta,
    double Gamma,
    double Vega,
    double Theta,
    DateTime Time);

/// <summary>Current portfolio exposure, used by pre-trade risk to check delta and vega limits.</summary>
public interface IPortfolioRisk
{
    (double Delta, double Vega) Exposure(string clientId);

    /// <summary>Delta and vega added by buying (or selling) one contract.</summary>
    (double Delta, double Vega) PerContract(OptionContract contract, Side side);
}

/// <summary>
/// Keeps each client's positions from the trades the OMS reports (outright fills and spread leg fills), with average
/// cost and realized P&amp;L, and marks them to market on demand. Updates run on the OMS loop (as a listener), so they're
/// in the same order the client sees its fills; reads from other threads take a lock and get a snapshot.
/// Positions live in memory: a restart starts everyone flat (orders and executions are still in MySQL).
/// </summary>
public sealed class PositionKeeper(MarketDataCache marketData, TimeProvider? time = null) : IOmsListener, IPortfolioRisk
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<int, Position>> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _closedRealized = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public void OnExecutionReport(ExecutionReportEvent report)
    {
        if (report.ExecType != ExecType.Trade)
        {
            return;
        }

        if (report.Leg is { } leg)
        {
            Apply(report.Order.ClientId, leg.Contract, leg.Side, leg.Quantity, leg.Price);
        }
        else if (!report.Order.IsMultileg && report.LastQty > 0)
        {
            Apply(report.Order.ClientId, report.Order.Contract, report.Order.Side, report.LastQty, report.LastPx);
        }
    }

    public void OnCancelReject(CancelRejectEvent reject)
    {
    }

    public void Apply(string clientId, OptionContract contract, Side side, decimal quantity, decimal price)
    {
        lock (_lock)
        {
            if (!_clients.TryGetValue(clientId, out var book))
            {
                book = [];
                _clients[clientId] = book;
            }

            if (!book.TryGetValue(contract.Id, out var p))
            {
                p = new Position(contract);
                book[contract.Id] = p;
            }

            p.Trade(side == Side.Buy ? quantity : -quantity, price);
        }
    }

    /// <summary>
    /// Cash-settles every position in an expired expiry at intrinsic value against the underlying price, booking the
    /// difference to realized P&amp;L. Returns how many positions were settled.
    /// </summary>
    public int Settle(DateOnly expiry, IReadOnlyDictionary<string, double> spots)
    {
        var settled = 0;
        lock (_lock)
        {
            foreach (var (clientId, book) in _clients)
            {
                foreach (var p in book.Values.Where(p => p.Contract.Expiry == expiry).ToList())
                {
                    if (spots.TryGetValue(p.Contract.Underlying, out var spot))
                    {
                        var strike = (double)p.Contract.Strike;
                        var intrinsic = (decimal)Math.Max(0, p.Contract.Right == OptionRight.Call ? spot - strike : strike - spot);
                        p.Trade(-p.Quantity, Math.Round(intrinsic, 4));
                    }

                    _closedRealized[clientId] = _closedRealized.GetValueOrDefault(clientId) + p.Realized;
                    book.Remove(p.Contract.Id);
                    settled++;
                }
            }
        }

        return settled;
    }

    public IReadOnlyList<string> Clients
    {
        get
        {
            lock (_lock)
            {
                return [.. _clients.Keys];
            }
        }
    }

    public PortfolioView Snapshot(string clientId)
    {
        List<(OptionContract Contract, decimal Qty, decimal Avg, decimal Realized)> rows;
        decimal closed;
        lock (_lock)
        {
            rows = _clients.TryGetValue(clientId, out var book)
                ? [.. book.Values.Select(p => (p.Contract, p.Quantity, p.AvgCost, p.Realized))]
                : [];
            closed = _closedRealized.GetValueOrDefault(clientId);
        }

        var views = new List<PositionView>();
        foreach (var (contract, qty, avg, realized) in rows.OrderBy(r => r.Contract.Underlying).ThenBy(r => r.Contract.Expiry).ThenBy(r => r.Contract.Strike))
        {
            var q = marketData.Quote(contract.Id);
            var mark = q is { Bid: { } b, Ask: { } a } ? (b + a) / 2 : (decimal)(q?.Theo ?? 0);
            var mult = (double)qty * OptionContract.Multiplier;
            views.Add(new PositionView(contract, qty, Math.Round(avg, 4), Math.Round(mark, 4),
                Math.Round(mark * qty * OptionContract.Multiplier, 2),
                qty == 0 ? 0 : Math.Round((mark - avg) * qty * OptionContract.Multiplier, 2),
                Math.Round(realized, 2),
                Math.Round((q?.Delta ?? 0) * mult, 1), Math.Round((q?.Gamma ?? 0) * mult, 2), Math.Round((q?.Vega ?? 0) * mult, 2),
                Math.Round((q?.Theta ?? 0) * mult, 2)));
        }

        var realizedTotal = views.Sum(v => v.RealizedPnl) + Math.Round(closed, 2);
        var unrealized = views.Sum(v => v.UnrealizedPnl);
        return new PortfolioView(clientId, views, realizedTotal, unrealized, realizedTotal + unrealized,
            Math.Round(views.Sum(v => v.Delta), 1), Math.Round(views.Sum(v => v.Gamma), 2), Math.Round(views.Sum(v => v.Vega), 2),
            Math.Round(views.Sum(v => v.Theta), 2), _time.GetUtcNow().UtcDateTime);
    }

    public (double Delta, double Vega) Exposure(string clientId)
    {
        var p = Snapshot(clientId);
        return (p.Delta, p.Vega);
    }

    public (double Delta, double Vega) PerContract(OptionContract contract, Side side)
    {
        var q = marketData.Quote(contract.Id);
        var sign = side == Side.Buy ? 1 : -1;
        return (sign * (q?.Delta ?? 0) * OptionContract.Multiplier, sign * (q?.Vega ?? 0) * OptionContract.Multiplier);
    }

    /// <summary>Average-cost position: adding keeps a weighted average; reducing books realized P&amp;L; crossing zero restarts.</summary>
    private sealed class Position(OptionContract contract)
    {
        public OptionContract Contract { get; } = contract;
        public decimal Quantity { get; private set; }
        public decimal AvgCost { get; private set; }
        public decimal Realized { get; private set; }

        public void Trade(decimal signedQty, decimal price)
        {
            if (signedQty == 0)
            {
                return;
            }

            if (Quantity == 0 || Math.Sign(Quantity) == Math.Sign(signedQty))
            {
                AvgCost = ((AvgCost * Math.Abs(Quantity)) + (price * Math.Abs(signedQty))) / (Math.Abs(Quantity) + Math.Abs(signedQty));
                Quantity += signedQty;
                return;
            }

            var closing = Math.Min(Math.Abs(signedQty), Math.Abs(Quantity));
            Realized += (price - AvgCost) * closing * Math.Sign(Quantity) * OptionContract.Multiplier;
            var remaining = Quantity + signedQty;
            if (remaining != 0 && Math.Sign(remaining) != Math.Sign(Quantity))
            {
                AvgCost = price; // flipped through zero: the excess opens a new position at this price
            }

            Quantity = remaining;
            if (Quantity == 0)
            {
                AvgCost = 0;
            }
        }
    }
}
