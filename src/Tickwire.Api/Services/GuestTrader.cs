using System.Collections.Concurrent;
using System.Globalization;
using Tickwire.Engine;
using Tickwire.Fix;
using Tickwire.Fix.Session;
using Tickwire.Fix.Session.Store;
using Tickwire.Fix.Session.Transport;
using Tickwire.Persistence;
using Tickwire.Pricing;
using Tickwire.Venue;

namespace Tickwire.Api.Services;

/// <summary>What the client itself believes about an order, rebuilt only from ExecutionReports it received.</summary>
public sealed record ClientOrderState(string OrderId, string ClOrdID, char OrdStatus, decimal CumQty, decimal LeavesQty, decimal AvgPx,
    int ReportsSeen);

/// <summary>
/// The browser trader's FIX client. It runs server-side as a real initiator session and connects to the acceptor
/// through an in-memory pipe (the same accept path TCP clients use), so every click in the UI produces genuine FIX
/// traffic on both sides. It rebuilds its own view of each order from the ExecutionReports it receives, which lets
/// the UI show that the client reconciles after a chaos-induced gap.
/// </summary>
public sealed class GuestTrader : NullFixApplication, IAsyncDisposable
{
    private readonly FixInitiator _initiator;
    private readonly ConcurrentDictionary<string, ClientOrderState> _orders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _execIds = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly string _clOrdPrefix;
    private int _counter;

    public GuestTrader(ClientAccount account, SessionConfig config, string venueKey, FixAcceptor acceptor, WireTap tap, ISessionStore store,
        bool storeIsFresh, TimeProvider time, ILogger logger)
    {
        Account = account;
        Config = config;
        VenueKey = venueKey;
        _time = time;
        _clOrdPrefix = config.ClientCompId.Replace("GST-", string.Empty, StringComparison.Ordinal) + "-"
            + (time.GetUtcNow().ToUnixTimeSeconds() % 100_000).ToString("x", CultureInfo.InvariantCulture) + "-";
        Session = new FixSession(new SessionSettings
        {
            Id = new SessionId(config.BeginString, config.ClientCompId, config.VenueCompId),
            Role = SessionRole.Initiator,
            HeartBtInt = config.HeartBtInt,
        }, store, this, time, logger, tap.Observer(venueKey, "client", isVenueSide: false))
        {
            // A client that lost its state (fresh in-memory store) asks the venue to start both sides from 1.
            ResetNextLogon = storeIsFresh,
        };
        Session.Disconnected += OnDisconnected;
        _initiator = new FixInitiator(Session, ct =>
        {
            var (venueSide, clientSide) = PipeTransport.CreatePair($"venue:{config.ClientCompId}", $"browser:{config.ClientCompId}");
            _ = acceptor.AcceptAsync(venueSide, CancellationToken.None);
            return Task.FromResult<IFixTransport>(clientSide);
        }, TimeSpan.FromSeconds(3), time)
        {
            EnableChaos = true,
        };
    }

    public ClientAccount Account { get; }
    public SessionConfig Config { get; }
    public string VenueKey { get; }
    public FixSession Session { get; }

    public IReadOnlyDictionary<string, ClientOrderState> ClientView => _orders;

    public async Task StartAsync()
    {
        await Session.StartAsync().ConfigureAwait(false);
        _initiator.Start();
    }

    public string NewClOrdId() => _clOrdPrefix + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture);

    public string SendNewOrder(OptionContract contract, Side side, OrderType type, TimeInForce tif, decimal? price, decimal quantity,
        string? destination = null)
    {
        var clOrdId = NewClOrdId();
        var b = new FixMessageBuilder(MsgTypes.NewOrderSingle);
        b.Set(Tags.ClOrdID, clOrdId).Set(Tags.Account, Account.ClientId).Set(Tags.HandlInst, '1');
        AppendInstrument(b, contract);
        b.Set(Tags.Side, side == Side.Buy ? '1' : '2')
            .SetUtcTimestamp(Tags.TransactTime, Now)
            .Set(Tags.OrderQty, quantity)
            .Set(Tags.OrdType, type == OrderType.Market ? '1' : '2');
        if (type == OrderType.Limit && price is { } px)
        {
            b.Set(Tags.Price, px);
        }

        b.Set(Tags.TimeInForce, (char)('0' + (int)tif)).Set(Tags.PositionEffect, 'O');
        if (destination is { Length: > 0 } && !destination.Equals("SMART", StringComparison.OrdinalIgnoreCase))
        {
            b.Set(Tags.ExDestination, destination.ToUpperInvariant());
        }

        Session.Send(b);
        return clOrdId;
    }

    /// <summary>NewOrderMultileg(AB): a net-priced limit order on 2-4 legs, legs in the NoLegs(555) group.</summary>
    public string SendMultileg(IReadOnlyList<OrderLeg> legs, Side side, TimeInForce tif, decimal price, decimal quantity)
    {
        var clOrdId = NewClOrdId();
        var b = new FixMessageBuilder(MsgTypes.NewOrderMultileg);
        b.Set(Tags.ClOrdID, clOrdId).Set(Tags.Account, Account.ClientId).Set(Tags.Side, side == Side.Buy ? '1' : '2')
            .Set(Tags.Symbol, legs[0].Contract.Underlying);
        FixOrderGateway.AppendLegs(b, legs);
        b.SetUtcTimestamp(Tags.TransactTime, Now)
            .Set(Tags.OrderQty, quantity)
            .Set(Tags.OrdType, '2')
            .Set(Tags.Price, price)
            .Set(Tags.TimeInForce, (char)('0' + (int)tif));
        Session.Send(b);
        return clOrdId;
    }

    public string SendCancel(OrderView order)
    {
        var clOrdId = NewClOrdId();
        var b = new FixMessageBuilder(MsgTypes.OrderCancelRequest);
        b.Set(Tags.OrigClOrdID, order.ClOrdID).Set(Tags.OrderID, order.OrderId).Set(Tags.ClOrdID, clOrdId);
        if (order.IsMultileg)
        {
            b.Set(Tags.Symbol, order.Contract.Underlying);
        }
        else
        {
            AppendInstrument(b, order.Contract);
        }

        b.Set(Tags.Side, order.Side == Side.Buy ? '1' : '2').SetUtcTimestamp(Tags.TransactTime, Now).Set(Tags.OrderQty, order.OrderQty);
        Session.Send(b);
        return clOrdId;
    }

    public string SendReplace(OrderView order, decimal? price, decimal quantity)
    {
        var clOrdId = NewClOrdId();
        var b = new FixMessageBuilder(MsgTypes.OrderCancelReplaceRequest);
        b.Set(Tags.OrderID, order.OrderId).Set(Tags.OrigClOrdID, order.ClOrdID).Set(Tags.ClOrdID, clOrdId).Set(Tags.HandlInst, '1');
        AppendInstrument(b, order.Contract);
        b.Set(Tags.Side, order.Side == Side.Buy ? '1' : '2')
            .SetUtcTimestamp(Tags.TransactTime, Now)
            .Set(Tags.OrderQty, quantity)
            .Set(Tags.OrdType, order.OrdType == OrderType.Market ? '1' : '2');
        if (price is { } px)
        {
            b.Set(Tags.Price, px);
        }

        Session.Send(b);
        return clOrdId;
    }

    /// <summary>Chaos: sends a TestRequest so a fault queued on the client's transport takes effect right away.</summary>
    public void Poke(string id)
    {
        var tr = new FixMessageBuilder(MsgTypes.TestRequest);
        tr.Set(Tags.TestReqID, id);
        Session.Send(tr);
    }

    /// <summary>Chaos: an order with an invalid Side(54), which the venue must answer with a session-level Reject(3).</summary>
    public void SendInvalidOrder(OptionContract contract)
    {
        var b = new FixMessageBuilder(MsgTypes.NewOrderSingle);
        b.Set(Tags.ClOrdID, NewClOrdId()).Set(Tags.HandlInst, '1');
        AppendInstrument(b, contract);
        b.Set(Tags.Side, 'Z').SetUtcTimestamp(Tags.TransactTime, Now).Set(Tags.OrderQty, 1m).Set(Tags.OrdType, '2').Set(Tags.Price, 1m);
        Session.Send(b);
    }

    public override ValueTask OnMessageAsync(FixSession session, FixMessage message, CancellationToken cancellationToken)
    {
        if (message.MsgType != MsgTypes.ExecutionReport)
        {
            return ValueTask.CompletedTask;
        }

        var execId = message.GetString(Tags.ExecID);
        var orderId = message.GetString(Tags.OrderID);
        if (execId is null || orderId is null or "NONE" || !_execIds.TryAdd(execId, 0))
        {
            return ValueTask.CompletedTask; // a PossDup resend of a report we already applied
        }

        _orders.AddOrUpdate(orderId,
            _ => State(message, orderId, 1),
            (_, prev) => State(message, orderId, prev.ReportsSeen + 1));
        return ValueTask.CompletedTask;
    }

    private static ClientOrderState State(FixMessage m, string orderId, int seen) => new(orderId, m.GetString(Tags.ClOrdID) ?? "",
        m.GetChar(Tags.OrdStatus) ?? '?', m.GetDecimal(Tags.CumQty) ?? 0, m.GetDecimal(Tags.LeavesQty) ?? 0, m.GetDecimal(Tags.AvgPx) ?? 0,
        seen);

    private void OnDisconnected(FixSession session, string reason)
    {
        // Chaos recovery the way an operator would do it: resume heartbeats; if the venue said our sequence number
        // was too low, our state is wrong, so the next Logon resets both sides.
        session.SuppressHeartbeats = false;
        if (reason.Contains("too low", StringComparison.OrdinalIgnoreCase))
        {
            session.ResetNextLogon = true;
        }
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static void AppendInstrument(FixMessageBuilder b, OptionContract c) =>
        b.Set(Tags.Symbol, c.Underlying)
            .Set(Tags.SecurityType, "OPT")
            .SetLocalMktDate(Tags.MaturityDate, c.Expiry)
            .Set(Tags.PutOrCall, c.Right == OptionRight.Call ? 1 : 0)
            .Set(Tags.StrikePrice, c.Strike);

    private int _disposeState1;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState1, 1) != 0)
        {
            return;
        }

        Session.Logout("Guest session closed");
        await Task.Delay(50).ConfigureAwait(false);
        await _initiator.DisposeAsync().ConfigureAwait(false);
        await Session.DisposeAsync().ConfigureAwait(false);
    }
}
