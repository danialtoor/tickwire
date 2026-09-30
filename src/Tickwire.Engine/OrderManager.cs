using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickwire.Venue;

namespace Tickwire.Engine;

public sealed record NewOrderRequest(
    ClientAccount Client,
    string ClOrdID,
    OptionContract? Contract,
    string? InstrumentError,
    Side Side,
    OrderType Type,
    TimeInForce TimeInForce,
    decimal? Price,
    decimal Quantity,
    string? Account,
    long ReceivedTimestamp)
{
    /// <summary>What the client sent as the symbol, echoed on rejects when the instrument could not be resolved.</summary>
    public string? RawSymbol { get; init; }

    /// <summary>ExDestination(100): an exchange code to send the order to directly, or null for the smart order router.</summary>
    public string? Destination { get; init; }
}

/// <summary>A multi-leg (spread) order: NewOrderMultileg(AB). Price is the net strategy price per unit.</summary>
public sealed record NewSpreadRequest(
    ClientAccount Client,
    string ClOrdID,
    IReadOnlyList<OrderLeg> Legs,
    string? InstrumentError,
    Side Side,
    TimeInForce TimeInForce,
    decimal? Price,
    decimal Quantity,
    string? Account,
    long ReceivedTimestamp);

public sealed record CancelOrderRequest(ClientAccount Client, string ClOrdID, string OrigClOrdID, long ReceivedTimestamp);

public sealed record ReplaceOrderRequest(ClientAccount Client, string ClOrdID, string OrigClOrdID, decimal? Price, decimal Quantity,
    long ReceivedTimestamp);

public sealed record OrderStatusQuery(ClientAccount Client, string ClOrdID);

/// <summary>Everything the OMS produces. Called on the OMS loop; implementations should hand work off quickly.</summary>
public interface IOmsListener
{
    void OnExecutionReport(ExecutionReportEvent report);

    void OnCancelReject(CancelRejectEvent reject);

    void OnRiskReject(string clientId, RiskRejectCode code, string text)
    {
    }
}

/// <summary>The parts of the venue the OMS uses. <see cref="SimulatedVenue"/> in production, fakes in tests.</summary>
public interface IExecutionVenue
{
    void Submit(long orderId, OptionContract contract, Side side, OrderType type, TimeInForce tif, decimal? price, decimal quantity,
        string? destination);

    void Cancel(long orderId, OptionContract contract);

    void Replace(long orderId, OptionContract contract, decimal price, decimal newTotalQuantity);

    void SubmitSpread(long orderId, IReadOnlyList<OrderLeg> legs, Side side, TimeInForce tif, decimal limit, decimal units);
}

public sealed class SimulatedVenueAdapter(SimulatedVenue venue) : IExecutionVenue
{
    public void Submit(long orderId, OptionContract contract, Side side, OrderType type, TimeInForce tif, decimal? price, decimal quantity,
        string? destination) =>
        venue.Submit(orderId, contract, side, type, tif, price, quantity, destination);

    public void Cancel(long orderId, OptionContract contract) => venue.Cancel(orderId, contract);

    public void Replace(long orderId, OptionContract contract, decimal price, decimal newTotalQuantity) =>
        venue.Replace(orderId, contract, price, newTotalQuantity);

    public void SubmitSpread(long orderId, IReadOnlyList<OrderLeg> legs, Side side, TimeInForce tif, decimal limit, decimal units) =>
        venue.SubmitSpread(orderId, legs[0].Contract.Underlying, [.. legs.Select(l => new SpreadLeg(l.Contract.Id, l.Ratio, l.Side))], side,
            tif, limit, units);
}

/// <summary>
/// The order management system. Owns every client order and applies risk, the order state machine and venue results.
/// Runs on one loop fed by a channel (client requests and venue events share it), so order state needs no locks and
/// every client sees its reports in a consistent order.
/// </summary>
public sealed partial class OrderManager : IVenueEventSink, IAsyncDisposable
{
    private readonly IExecutionVenue _venue;
    private readonly MarketDataCache _marketData;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly List<IOmsListener> _listeners = [];
    private readonly Channel<object> _inbox = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<long, Order> _orders = [];
    private readonly Dictionary<(string Client, string ClOrdID), Order> _byClOrdId = [];
    private readonly Dictionary<string, HashSet<long>> _openByClient = [];
    private readonly Dictionary<long, long> _pendingReceived = [];
    private readonly MessageThrottle _throttle;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private long _nextOrderId;
    private long _nextExecId;
    private volatile bool _globalKillSwitch;
    private readonly string _execIdPrefix;

    /// <param name="firstOrderId">First internal order number. Hosts pass a value that can't collide with orders stored by a previous run.</param>
    /// <param name="execIdPrefix">Makes ExecIDs unique across restarts (FIX requires ExecID to be unique per trading day).</param>
    public OrderManager(IExecutionVenue venue, MarketDataCache marketData, TimeProvider? time = null, ILogger? logger = null,
        long firstOrderId = 1, string execIdPrefix = "")
    {
        _execIdPrefix = execIdPrefix;
        _venue = venue;
        _marketData = marketData;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _throttle = new MessageThrottle(_time);
        _nextOrderId = firstOrderId - 1;
    }

    public bool GlobalKillSwitch => _globalKillSwitch;

    public void AddListener(IOmsListener listener) => _listeners.Add(listener);

    /// <summary>Enables delta/vega limits. The source must be updated on this loop (register it as a listener too).</summary>
    public IPortfolioRisk? PortfolioRisk { get; set; }

    public void Start() => _loop ??= Task.Run(RunAsync);

    public void Submit(NewOrderRequest request) => _inbox.Writer.TryWrite(request);

    public void Submit(NewSpreadRequest request) => _inbox.Writer.TryWrite(request);

    public void Submit(CancelOrderRequest request) => _inbox.Writer.TryWrite(request);

    public void Submit(ReplaceOrderRequest request) => _inbox.Writer.TryWrite(request);

    public void Submit(OrderStatusQuery request) => _inbox.Writer.TryWrite(request);

    public void OnVenueEvent(VenueEvent e) => _inbox.Writer.TryWrite(e);

    /// <summary>Blocks new orders and cancels every open order, for one client or (clientId null) everyone.</summary>
    public Task<int> KillSwitchAsync(string? clientId, bool engaged, string reason) => InvokeAsync(() =>
    {
        if (clientId is null)
        {
            _globalKillSwitch = engaged;
        }

        return engaged ? CancelAllOpen(clientId, reason) : 0;
    });

    public Task<int> CancelAllAsync(string clientId, string reason) => InvokeAsync(() => CancelAllOpen(clientId, reason));

    public Task<IReadOnlyList<OrderView>> OrdersAsync(string? clientId = null, bool openOnly = false, int max = 500) =>
        InvokeAsync<IReadOnlyList<OrderView>>(() =>
        [
            .. _orders.Values
                .Where(o => (clientId is null || o.ClientId == clientId) && (!openOnly || o.IsOpen))
                .OrderByDescending(o => o.Id)
                .Take(max)
                .Select(o => o.View()),
        ]);

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inbox.Writer.TryWrite(new Invoke(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        return tcs.Task;
    }

    public Task FlushAsync() => InvokeAsync(() => true);

    private int _disposeState1;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState1, 1) != 0)
        {
            return;
        }

        _inbox.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private async Task RunAsync()
    {
        var reader = _inbox.Reader;
        while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                try
                {
                    Handle(item);
                }
                catch (Exception ex)
                {
                    OmsError(_logger, ex, item.GetType().Name);
                }
            }
        }
    }

    private void Handle(object item)
    {
        switch (item)
        {
            case NewOrderRequest r:
                OnNewOrder(r);
                break;
            case NewSpreadRequest r:
                OnNewSpread(r);
                break;
            case CancelOrderRequest r:
                OnCancel(r);
                break;
            case ReplaceOrderRequest r:
                OnReplace(r);
                break;
            case OrderStatusQuery q:
                OnStatus(q);
                break;
            case OrderSubmitted e:
                OnSubmitted(e);
                break;
            case PassiveFill e:
                OnPassiveFill(e);
                break;
            case OrderCanceled e:
                OnCanceled(e);
                break;
            case CancelFailed e:
                OnCancelFailed(e);
                break;
            case OrderReplaced e:
                OnReplaced(e);
                break;
            case ReplaceFailed e:
                OnReplaceFailed(e);
                break;
            case OrderExpired e:
                OnExpired(e);
                break;
            case SpreadSubmitted e:
                OnSpreadSubmitted(e);
                break;
            case SpreadFilled e:
                if (_orders.TryGetValue(e.OrderId, out var spreadOrder) && spreadOrder.IsOpen)
                {
                    ApplySpreadExecution(spreadOrder, e.Execution);
                }

                break;
            case Invoke i:
                i.Action();
                break;
            default:
                break;
        }
    }

    // ------------------------------------------------------------------ client requests

    private void OnNewOrder(NewOrderRequest r)
    {
        var client = r.Client;
        var limits = client.Limits;

        RiskResult? reject = null;
        if (_globalKillSwitch || client.KillSwitch)
        {
            reject = new(RiskRejectCode.KillSwitch, "Kill switch engaged: new orders are blocked", OrdRejReason.BrokerOption);
        }
        else if (!_throttle.TryAcquire(client.ClientId, limits.MaxMessagesPerSecond))
        {
            reject = new(RiskRejectCode.Throttle, $"Message rate above {limits.MaxMessagesPerSecond}/s", OrdRejReason.BrokerOption);
        }
        else if (_byClOrdId.ContainsKey((client.ClientId, r.ClOrdID)))
        {
            reject = new(RiskRejectCode.DuplicateClOrdID, $"Duplicate ClOrdID {r.ClOrdID}", OrdRejReason.DuplicateOrder);
        }
        else if (r.Contract is null)
        {
            reject = new(RiskRejectCode.UnknownInstrument, r.InstrumentError ?? "Unknown instrument", OrdRejReason.UnknownSymbol);
        }
        else
        {
            var quote = _marketData.Quote(r.Contract.Id);
            var opposite = r.Side == Side.Buy ? quote?.Ask : quote?.Bid;
            reject = PreTradeRisk.Check(limits, r.Contract, r.Type, r.TimeInForce, r.Price, r.Quantity, OpenCount(client.ClientId),
                quote?.Theo, opposite);
            if (reject is null && PortfolioRisk is { } pr)
            {
                var per = pr.PerContract(r.Contract, r.Side);
                reject = PortfolioLimits.Check(limits, pr.Exposure(client.ClientId), (per.Delta * (double)r.Quantity, per.Vega * (double)r.Quantity));
            }
        }

        if (reject is { } rr)
        {
            RejectNew(r, rr);
            return;
        }

        var order = new Order
        {
            Id = ++_nextOrderId,
            ClientId = client.ClientId,
            ClOrdID = r.ClOrdID,
            Contract = r.Contract!,
            Side = r.Side,
            OrdType = r.Type,
            TimeInForce = r.TimeInForce,
            Price = r.Type == OrderType.Limit ? r.Price : null,
            OrderQty = r.Quantity,
            LeavesQty = r.Quantity,
            Account = r.Account,
            Destination = r.Destination,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _orders[order.Id] = order;
        _byClOrdId[(client.ClientId, order.ClOrdID)] = order;
        OpenSet(client.ClientId).Add(order.Id);
        _pendingReceived[order.Id] = r.ReceivedTimestamp;

        Report(order, ExecType.PendingNew);
        _venue.Submit(order.Id, order.Contract, order.Side, order.OrdType, order.TimeInForce, order.Price, order.OrderQty, order.Destination);
    }

    private void OnNewSpread(NewSpreadRequest r)
    {
        var client = r.Client;
        var limits = client.Limits;
        RiskResult? reject = null;
        if (_globalKillSwitch || client.KillSwitch)
        {
            reject = new(RiskRejectCode.KillSwitch, "Kill switch engaged: new orders are blocked", OrdRejReason.BrokerOption);
        }
        else if (!_throttle.TryAcquire(client.ClientId, limits.MaxMessagesPerSecond))
        {
            reject = new(RiskRejectCode.Throttle, $"Message rate above {limits.MaxMessagesPerSecond}/s", OrdRejReason.BrokerOption);
        }
        else if (_byClOrdId.ContainsKey((client.ClientId, r.ClOrdID)))
        {
            reject = new(RiskRejectCode.DuplicateClOrdID, $"Duplicate ClOrdID {r.ClOrdID}", OrdRejReason.DuplicateOrder);
        }
        else if (r.InstrumentError is not null)
        {
            reject = new(RiskRejectCode.UnknownInstrument, r.InstrumentError, OrdRejReason.UnknownSymbol);
        }
        else
        {
            reject = PreTradeRisk.CheckSpread(limits, r.Legs, r.TimeInForce, r.Price, r.Quantity, OpenCount(client.ClientId),
                id => _marketData.Quote(id)?.Theo);
            if (reject is null && PortfolioRisk is { } pr)
            {
                var exposure = r.Legs.Aggregate((Delta: 0.0, Vega: 0.0), (acc, l) =>
                {
                    var legSide = r.Side == Side.Buy ? l.Side : (l.Side == Side.Buy ? Side.Sell : Side.Buy);
                    var per = pr.PerContract(l.Contract, legSide);
                    var n = (double)r.Quantity * l.Ratio;
                    return (acc.Delta + (per.Delta * n), acc.Vega + (per.Vega * n));
                });
                reject = PortfolioLimits.Check(limits, pr.Exposure(client.ClientId), exposure);
            }
        }

        if (reject is { } rr)
        {
            foreach (var l in _listeners)
            {
                l.OnRiskReject(client.ClientId, rr.Code, rr.Text);
            }

            var first = r.Legs.Count > 0 ? r.Legs[0].Contract : new OptionContract(0, "UNKNOWN", DateOnly.MinValue, Pricing.OptionRight.Call, 0);
            var view = new OrderView(0, "NONE", client.ClientId, r.ClOrdID, null, first, r.Side, OrderType.Limit, r.TimeInForce, r.Price,
                r.Quantity, 0, 0, 0, OrdStatus.Rejected, r.Account, Now, Now, rr.Text) { Legs = r.Legs.Count > 0 ? r.Legs : null };
            Emit(new ExecutionReportEvent(view, ExecType.Rejected, NextExecId(), 0, 0, rr.Text, rr.FixReason, Now, r.ReceivedTimestamp));
            return;
        }

        var order = new Order
        {
            Id = ++_nextOrderId,
            ClientId = client.ClientId,
            ClOrdID = r.ClOrdID,
            Contract = r.Legs[0].Contract,
            Legs = r.Legs,
            Side = r.Side,
            OrdType = OrderType.Limit,
            TimeInForce = r.TimeInForce,
            Price = r.Price,
            OrderQty = r.Quantity,
            LeavesQty = r.Quantity,
            Account = r.Account,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _orders[order.Id] = order;
        _byClOrdId[(client.ClientId, order.ClOrdID)] = order;
        OpenSet(client.ClientId).Add(order.Id);
        _pendingReceived[order.Id] = r.ReceivedTimestamp;
        Report(order, ExecType.PendingNew);
        _venue.SubmitSpread(order.Id, r.Legs, r.Side, r.TimeInForce, r.Price!.Value, r.Quantity);
    }

    private void OnSpreadSubmitted(SpreadSubmitted e)
    {
        if (!_orders.TryGetValue(e.OrderId, out var order))
        {
            return;
        }

        _pendingReceived.Remove(order.Id, out var received);
        order.Exchange = "SMART"; // legs go to whichever exchange has the best price for each
        order.Apply(OrderEvent.Accept);
        Report(order, ExecType.New, received: received);
        foreach (var execution in e.Executions)
        {
            ApplySpreadExecution(order, execution);
        }

        if (e.CanceledUnits > 0 && order.IsOpen)
        {
            order.LeavesQty = 0;
            order.Apply(OrderEvent.Cancel);
            Report(order, ExecType.Canceled, text: e.CancelReason);
        }
    }

    /// <summary>One strategy-level report (MultiLegReportingType=3) and one report per leg trade (=2).</summary>
    private void ApplySpreadExecution(Order order, SpreadExecution execution)
    {
        order.AddFill(execution.StrategyPrice, execution.Units);
        Report(order, ExecType.Trade, lastQty: execution.Units, lastPx: execution.StrategyPrice);
        // Each leg takes liquidity from the outright book of whichever exchange had the best price for it.
        foreach (var leg in execution.Legs)
        {
            var contract = order.Legs!.First(l => l.Contract.Id == leg.ContractId).Contract;
            var exchange = Exchanges.Find(leg.Exchange) ?? Exchanges.Primary;
            var fee = exchange.TakerFee * leg.Quantity;
            order.Fees += fee;
            var view = order.View();
            Emit(new ExecutionReportEvent(view, ExecType.Trade, NextExecId(), leg.Quantity, leg.Price, null, null, Now, null)
            {
                Leg = new LegExecution(contract, leg.Side, leg.Quantity, leg.Price),
                LastMkt = exchange.Code,
                LastLiquidity = Liquidity.Removed,
                Commission = fee,
            });
        }
    }

    private void RejectNew(NewOrderRequest r, RiskResult reject)
    {
        foreach (var l in _listeners)
        {
            l.OnRiskReject(r.Client.ClientId, reject.Code, reject.Text);
        }

        // A rejected order still gets an ExecutionReport so the client can reconcile. It is not stored, and a duplicate
        // ClOrdID reject must not replace the original order in the ClOrdID index.
        var view = new OrderView(0, "NONE", r.Client.ClientId, r.ClOrdID, null,
            r.Contract ?? new OptionContract(0, r.RawSymbol ?? "UNKNOWN", DateOnly.MinValue, Pricing.OptionRight.Call, 0), r.Side, r.Type,
            r.TimeInForce, r.Price, r.Quantity, 0, 0, 0, OrdStatus.Rejected, r.Account, Now, Now, reject.Text);
        Emit(new ExecutionReportEvent(view, ExecType.Rejected, NextExecId(), 0, 0, reject.Text, reject.FixReason, Now,
            r.ReceivedTimestamp));
    }

    private void OnCancel(CancelOrderRequest r)
    {
        if (!_throttle.TryAcquire(r.Client.ClientId, r.Client.Limits.MaxMessagesPerSecond))
        {
            CancelReject(r.Client.ClientId, r.ClOrdID, r.OrigClOrdID, null, CxlRejReason.BrokerOption, false, "Message rate limit exceeded");
            return;
        }

        if (!TryFindForChange(r.Client.ClientId, r.ClOrdID, r.OrigClOrdID, false, out var order))
        {
            return;
        }

        order.Pending = new PendingChange(true, r.ClOrdID, order.ClOrdID, null, order.OrderQty);
        _byClOrdId[(order.ClientId, r.ClOrdID)] = order;
        order.Apply(OrderEvent.RequestCancel);
        Report(order, ExecType.PendingCancel, clOrdIdOverride: r.ClOrdID, origOverride: order.ClOrdID);
        _venue.Cancel(order.Id, order.Contract);
    }

    private void OnReplace(ReplaceOrderRequest r)
    {
        if (!_throttle.TryAcquire(r.Client.ClientId, r.Client.Limits.MaxMessagesPerSecond))
        {
            CancelReject(r.Client.ClientId, r.ClOrdID, r.OrigClOrdID, null, CxlRejReason.BrokerOption, true, "Message rate limit exceeded");
            return;
        }

        if (!TryFindForChange(r.Client.ClientId, r.ClOrdID, r.OrigClOrdID, true, out var order))
        {
            return;
        }

        if (order.Legs is not null)
        {
            CancelReject(order.ClientId, r.ClOrdID, r.OrigClOrdID, order, CxlRejReason.Other, true,
                "Multi-leg orders can't be replaced; cancel and send a new NewOrderMultileg");
            return;
        }

        if (r.Quantity <= order.CumQty)
        {
            CancelReject(order.ClientId, r.ClOrdID, r.OrigClOrdID, order, CxlRejReason.Other, true,
                $"New OrderQty {r.Quantity} must be greater than CumQty {order.CumQty}");
            return;
        }

        var newPrice = order.OrdType == OrderType.Limit ? r.Price ?? order.Price : null;
        if (order.OrdType == OrderType.Limit)
        {
            var quote = _marketData.Quote(order.Contract.Id);
            var opposite = order.Side == Side.Buy ? quote?.Ask : quote?.Bid;
            // Size limits apply to what could still trade: the new open quantity.
            if (PreTradeRisk.Check(r.Client.Limits, order.Contract, order.OrdType, order.TimeInForce, newPrice, r.Quantity - order.CumQty,
                    0, quote?.Theo, opposite) is { } risk)
            {
                foreach (var l in _listeners)
                {
                    l.OnRiskReject(order.ClientId, risk.Code, risk.Text);
                }

                CancelReject(order.ClientId, r.ClOrdID, r.OrigClOrdID, order, CxlRejReason.BrokerOption, true, risk.Text);
                return;
            }
        }

        order.Pending = new PendingChange(false, r.ClOrdID, order.ClOrdID, newPrice, r.Quantity);
        _byClOrdId[(order.ClientId, r.ClOrdID)] = order;
        order.Apply(OrderEvent.RequestReplace);
        Report(order, ExecType.PendingReplace, clOrdIdOverride: r.ClOrdID, origOverride: order.ClOrdID);
        _venue.Replace(order.Id, order.Contract, newPrice ?? 0, r.Quantity);
    }

    private bool TryFindForChange(string clientId, string clOrdId, string origClOrdId, bool forReplace, out Order order)
    {
        order = null!;
        if (_byClOrdId.ContainsKey((clientId, clOrdId)))
        {
            CancelReject(clientId, clOrdId, origClOrdId, null, CxlRejReason.DuplicateClOrdID, forReplace, $"Duplicate ClOrdID {clOrdId}");
            return false;
        }

        if (!_byClOrdId.TryGetValue((clientId, origClOrdId), out var found))
        {
            CancelReject(clientId, clOrdId, origClOrdId, null, CxlRejReason.UnknownOrder, forReplace, $"Unknown order: OrigClOrdID {origClOrdId}");
            return false;
        }

        if (found.IsTerminal)
        {
            CancelReject(clientId, clOrdId, origClOrdId, found, CxlRejReason.TooLateToCancel, forReplace,
                $"Too late to {(forReplace ? "replace" : "cancel")}: order is {found.Status}");
            return false;
        }

        if (found.Pending is not null)
        {
            CancelReject(clientId, clOrdId, origClOrdId, found, CxlRejReason.AlreadyPending, forReplace,
                "Order already has a pending cancel or replace");
            return false;
        }

        if (found.Status == OrdStatus.PendingNew)
        {
            CancelReject(clientId, clOrdId, origClOrdId, found, CxlRejReason.AlreadyPending, forReplace,
                "Order not yet acknowledged by the venue");
            return false;
        }

        if (found.ClOrdID != origClOrdId)
        {
            // FIX requires OrigClOrdID to be the most recent ClOrdID in a replace chain.
            CancelReject(clientId, clOrdId, origClOrdId, found, CxlRejReason.UnknownOrder, forReplace,
                $"OrigClOrdID {origClOrdId} is stale; the order's current ClOrdID is {found.ClOrdID}");
            return false;
        }

        order = found;
        return true;
    }

    private void OnStatus(OrderStatusQuery q)
    {
        if (_byClOrdId.TryGetValue((q.Client.ClientId, q.ClOrdID), out var order))
        {
            Report(order, ExecType.OrderStatus);
        }
        else
        {
            var view = new OrderView(0, "NONE", q.Client.ClientId, q.ClOrdID, null,
                new OptionContract(0, "UNKNOWN", DateOnly.MinValue, Pricing.OptionRight.Call, 0), Side.Buy, OrderType.Limit,
                TimeInForce.Day, null, 0, 0, 0, 0, OrdStatus.Rejected, null, Now, Now, "Unknown order");
            Emit(new ExecutionReportEvent(view, ExecType.OrderStatus, NextExecId(), 0, 0, "Unknown order", OrdRejReason.UnknownOrder, Now,
                null));
        }
    }

    // ------------------------------------------------------------------ venue events

    private void OnSubmitted(OrderSubmitted e)
    {
        if (!_orders.TryGetValue(e.OrderId, out var order))
        {
            return;
        }

        _pendingReceived.Remove(order.Id, out var received);
        order.Exchange = e.Exchange;
        order.Apply(OrderEvent.Accept);
        Report(order, ExecType.New, received: received, routeReason: e.RouteReason);

        foreach (var fill in e.Fills)
        {
            ApplyFill(order, fill.Price, fill.Quantity, Liquidity.Removed);
        }

        if (e.CanceledQuantity > 0 && order.IsOpen)
        {
            order.LeavesQty = 0;
            order.Apply(OrderEvent.Cancel);
            Report(order, ExecType.Canceled, text: e.CancelReason);
        }
    }

    private void OnPassiveFill(PassiveFill e)
    {
        if (_orders.TryGetValue(e.OrderId, out var order) && order.IsOpen)
        {
            ApplyFill(order, e.Price, e.Quantity, Liquidity.Added);
        }
    }

    private void ApplyFill(Order order, decimal price, decimal quantity, Liquidity liquidity)
    {
        // Fills during a pending cancel/replace still belong to the order's current ClOrdID: the request isn't accepted yet.
        order.AddFill(price, quantity);
        var exchange = Exchanges.Find(order.Exchange) ?? Exchanges.Primary;
        var fee = exchange.Fee(liquidity) * quantity;
        order.Fees += fee;
        Report(order, ExecType.Trade, lastQty: quantity, lastPx: price, fill: (exchange.Code, liquidity, fee));
        if (order.Status == OrdStatus.Filled)
        {
            order.Pending = null;
        }
    }

    private void OnCanceled(OrderCanceled e)
    {
        if (!_orders.TryGetValue(e.OrderId, out var order) || order.IsTerminal)
        {
            return;
        }

        var pending = order.Pending;
        if (pending is not null)
        {
            order.OrigClOrdID = order.ClOrdID;
            order.ClOrdID = pending.ClOrdID;
        }

        order.Pending = null;
        order.LeavesQty = 0;
        order.Apply(OrderEvent.Cancel);
        Report(order, ExecType.Canceled, text: pending is null ? order.Text : null);
    }

    private void OnCancelFailed(CancelFailed e)
    {
        if (!_orders.TryGetValue(e.OrderId, out var order))
        {
            return;
        }

        var pending = order.Pending;
        if (pending is null || !pending.IsCancel)
        {
            return; // an internal cancel (kill switch, disconnect) raced a fill
        }

        order.Pending = null;
        if (order.Status == OrdStatus.PendingCancel)
        {
            order.Apply(OrderEvent.RejectCancel);
        }

        CancelReject(order.ClientId, pending.ClOrdID, pending.OrigClOrdID, order, CxlRejReason.TooLateToCancel, false,
            order.Status == OrdStatus.Filled ? "Too late to cancel: order filled" : e.Reason);
    }

    private void OnReplaced(OrderReplaced e)
    {
        if (!_orders.TryGetValue(e.OrderId, out var order) || order.Pending is not { IsCancel: false } pending)
        {
            return;
        }

        order.OrigClOrdID = order.ClOrdID;
        order.ClOrdID = pending.ClOrdID;
        order.Price = pending.NewPrice;
        order.OrderQty = pending.NewQty;
        order.LeavesQty = pending.NewQty - order.CumQty;
        order.Pending = null;
        order.Apply(OrderEvent.Replace);
        Report(order, ExecType.Replaced);

        foreach (var fill in e.Fills)
        {
            ApplyFill(order, fill.Price, fill.Quantity, Liquidity.Removed);
        }
    }

    private void OnReplaceFailed(ReplaceFailed e)
    {
        if (!_orders.TryGetValue(e.OrderId, out var order) || order.Pending is not { IsCancel: false } pending)
        {
            return;
        }

        order.Pending = null;
        if (order.Status == OrdStatus.PendingReplace)
        {
            order.Apply(OrderEvent.RejectReplace);
        }

        CancelReject(order.ClientId, pending.ClOrdID, pending.OrigClOrdID, order, CxlRejReason.TooLateToCancel, true,
            order.Status == OrdStatus.Filled ? "Too late to replace: order filled" : e.Reason);
    }

    private void OnExpired(OrderExpired e)
    {
        if (!_orders.TryGetValue(e.OrderId, out var order) || order.IsTerminal)
        {
            return;
        }

        order.Pending = null;
        order.LeavesQty = 0;
        order.Apply(OrderEvent.Expire);
        Report(order, ExecType.Expired, text: $"Expired: {order.Contract.Display} stopped trading");
    }

    // ------------------------------------------------------------------ helpers

    private int CancelAllOpen(string? clientId, string reason)
    {
        var count = 0;
        foreach (var order in _orders.Values)
        {
            if ((clientId is null || order.ClientId == clientId) && order.IsOpen
                && order.Status is not (OrdStatus.PendingNew or OrdStatus.PendingCancel))
            {
                order.Text = reason;
                _venue.Cancel(order.Id, order.Contract);
                count++;
            }
        }

        return count;
    }

    private int OpenCount(string clientId) => _openByClient.TryGetValue(clientId, out var set) ? set.Count : 0;

    private HashSet<long> OpenSet(string clientId)
    {
        if (!_openByClient.TryGetValue(clientId, out var set))
        {
            set = [];
            _openByClient[clientId] = set;
        }

        return set;
    }

    private void Report(Order order, ExecType execType, decimal lastQty = 0, decimal lastPx = 0, string? text = null,
        string? clOrdIdOverride = null, string? origOverride = null, long? received = null, string? routeReason = null,
        (string Exchange, Liquidity Liquidity, decimal Fee)? fill = null)
    {
        order.UpdatedAt = Now;
        if (order.IsTerminal)
        {
            OpenSet(order.ClientId).Remove(order.Id);
        }

        var view = order.View();
        if (clOrdIdOverride is not null)
        {
            view = view with { ClOrdID = clOrdIdOverride, OrigClOrdID = origOverride };
        }

        Emit(new ExecutionReportEvent(view, execType, NextExecId(), lastQty, lastPx, text, null, Now, received)
        {
            RouteReason = routeReason,
            LastMkt = fill?.Exchange,
            LastLiquidity = fill?.Liquidity,
            Commission = fill?.Fee,
        });
    }

    private void CancelReject(string clientId, string clOrdId, string origClOrdId, Order? order, CxlRejReason reason, bool forReplace,
        string text)
    {
        var reject = new CancelRejectEvent(clientId, clOrdId, origClOrdId, order?.OrderId, order?.Status ?? OrdStatus.Rejected, reason,
            forReplace, text, Now);
        foreach (var l in _listeners)
        {
            l.OnCancelReject(reject);
        }
    }

    private void Emit(ExecutionReportEvent report)
    {
        foreach (var l in _listeners)
        {
            l.OnExecutionReport(report);
        }
    }

    private string NextExecId() => $"EX{_execIdPrefix}{++_nextExecId:D7}";

    [LoggerMessage(Level = LogLevel.Error, Message = "OMS failed handling {Item}")]
    private static partial void OmsError(ILogger logger, Exception ex, string item);

    private sealed record Invoke(Action Action);
}
