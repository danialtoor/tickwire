using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Tickwire.Fix;
using Tickwire.Fix.Session;
using Tickwire.Pricing;
using Tickwire.Venue;

namespace Tickwire.Engine;

/// <summary>
/// The FIX application layer: turns inbound FIX order messages into OMS requests and OMS results into
/// ExecutionReport(8) / OrderCancelReject(9) messages on the client's session.
/// Sessions carry their <see cref="ClientAccount"/> in <see cref="FixSession.Tag"/> (set when the session is resolved).
/// </summary>
public sealed class FixOrderGateway : IFixApplication, IOmsListener
{
    private readonly OrderManager _oms;
    private readonly InstrumentRegistry _instruments;
    private readonly MarketDataCache _marketData;
    private readonly EngineMetrics _metrics;
    private readonly ConcurrentDictionary<string, FixSession> _sessions = new(StringComparer.Ordinal);

    public FixOrderGateway(OrderManager oms, InstrumentRegistry instruments, MarketDataCache marketData, EngineMetrics metrics)
    {
        _oms = oms;
        _instruments = instruments;
        _marketData = marketData;
        _metrics = metrics;
    }

    /// <summary>Links a client to its session so reports can be routed even before the first logon.</summary>
    public void Register(ClientAccount client, FixSession session)
    {
        session.Tag = client;
        _sessions[client.ClientId] = session;
    }

    public void Unregister(string clientId) => _sessions.TryRemove(clientId, out _);

    public void OnLogon(FixSession session)
    {
        if (session.Tag is ClientAccount client)
        {
            _sessions[client.ClientId] = session;
        }
    }

    public void OnLogout(FixSession session)
    {
        if (session.Tag is ClientAccount { Limits.CancelOnDisconnect: true } client)
        {
            _ = _oms.CancelAllAsync(client.ClientId, "Canceled on disconnect");
        }
    }

    public ValueTask OnMessageAsync(FixSession session, FixMessage message, CancellationToken cancellationToken)
    {
        var received = Stopwatch.GetTimestamp();
        if (session.Tag is not ClientAccount client)
        {
            BusinessReject(session, message, BusinessRejectReason.ApplicationNotAvailable, "Session is not linked to a client account");
            return ValueTask.CompletedTask;
        }

        switch (message.MsgType)
        {
            case MsgTypes.NewOrderSingle:
                OnNewOrder(session, client, message, received);
                break;
            case MsgTypes.OrderCancelRequest:
                _oms.Submit(new CancelOrderRequest(client, message.GetString(Tags.ClOrdID)!, message.GetString(Tags.OrigClOrdID)!, received));
                break;
            case MsgTypes.OrderCancelReplaceRequest:
                _oms.Submit(new ReplaceOrderRequest(client, message.GetString(Tags.ClOrdID)!, message.GetString(Tags.OrigClOrdID)!,
                    message.GetDecimal(Tags.Price), message.GetDecimal(Tags.OrderQty) ?? 0, received));
                break;
            case MsgTypes.OrderStatusRequest:
                _oms.Submit(new OrderStatusQuery(client, message.GetString(Tags.ClOrdID)!));
                break;
            default:
                BusinessReject(session, message, BusinessRejectReason.UnsupportedMessageType,
                    $"MsgType {message.MsgType} is not supported by this gateway; see the Rules of Engagement");
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void OnNewOrder(FixSession session, ClientAccount client, FixMessage m, long received)
    {
        var side = m.GetChar(Tags.Side) switch
        {
            '1' => (Side?)Side.Buy,
            '2' => Side.Sell,
            _ => null,
        };
        var type = m.GetChar(Tags.OrdType) switch
        {
            '1' => (OrderType?)OrderType.Market,
            '2' => OrderType.Limit,
            _ => null,
        };
        var tif = m.GetChar(Tags.TimeInForce) switch
        {
            null or '0' => (TimeInForce?)TimeInForce.Day,
            '3' => TimeInForce.ImmediateOrCancel,
            '4' => TimeInForce.FillOrKill,
            _ => null,
        };

        if (side is null || type is null || tif is null)
        {
            var what = side is null ? $"Side(54)={m.GetString(Tags.Side)}" : type is null ? $"OrdType(40)={m.GetString(Tags.OrdType)}"
                : $"TimeInForce(59)={m.GetString(Tags.TimeInForce)}";
            BusinessReject(session, m, BusinessRejectReason.Other, $"Unsupported {what}; see the Rules of Engagement");
            return;
        }

        var contract = ResolveInstrument(m, out var error);
        _oms.Submit(new NewOrderRequest(client, m.GetString(Tags.ClOrdID)!, contract, error, side.Value, type.Value, tif.Value,
            m.GetDecimal(Tags.Price), m.GetDecimal(Tags.OrderQty) ?? 0, m.GetString(Tags.Account), received)
        {
            RawSymbol = m.GetString(Tags.Symbol),
        });
    }

    /// <summary>
    /// Accepts either an OCC symbol in Symbol(55) (e.g. "SPY   250620C00550000") or the underlying in Symbol(55) with
    /// SecurityType(167)=OPT, PutOrCall(201), StrikePrice(202) and MaturityDate(541).
    /// </summary>
    public OptionContract? ResolveInstrument(FixMessage m, out string? error)
    {
        error = null;
        var symbol = m.GetString(Tags.Symbol) ?? string.Empty;
        if (OccSymbol.TryParse(symbol, out _))
        {
            var byOcc = _instruments.Find(symbol);
            error = byOcc is null ? $"Unknown instrument: OCC symbol '{symbol}' is not listed" : null;
            return byOcc;
        }

        if (_instruments.Underlying(symbol) is null)
        {
            error = $"Unknown symbol '{symbol}'. Listed underlyings: {string.Join(", ", _instruments.Underlyings.Select(u => u.Symbol))}";
            return null;
        }

        var securityType = m.GetString(Tags.SecurityType);
        if (securityType is not null and not "OPT")
        {
            error = $"SecurityType(167)={securityType} is not supported; only OPT";
            return null;
        }

        var putCall = m.GetInt(Tags.PutOrCall);
        var strike = m.GetDecimal(Tags.StrikePrice);
        var maturity = m.GetString(Tags.MaturityDate) ?? m.GetString(Tags.MaturityMonthYear);
        if (putCall is not (0 or 1) || strike is null || maturity is null)
        {
            error = "Option orders need PutOrCall(201), StrikePrice(202) and MaturityDate(541), or an OCC symbol in Symbol(55)";
            return null;
        }

        if (!DateOnly.TryParseExact(maturity, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
        {
            error = $"MaturityDate '{maturity}' must be yyyyMMdd";
            return null;
        }

        var right = putCall == 1 ? OptionRight.Call : OptionRight.Put;
        var contract = _instruments.Find(symbol, expiry, right, strike.Value);
        if (contract is null)
        {
            error = $"Unknown instrument: {symbol} {expiry:yyyy-MM-dd} {strike} {(right == OptionRight.Call ? "call" : "put")} is not listed";
        }

        return contract;
    }

    // ------------------------------------------------------------------ OMS → FIX

    public void OnExecutionReport(ExecutionReportEvent report)
    {
        if (!_sessions.TryGetValue(report.Order.ClientId, out var session))
        {
            return;
        }

        var o = report.Order;
        var er = new FixMessageBuilder(MsgTypes.ExecutionReport, 512);
        er.Set(Tags.OrderID, o.OrderId)
            .Set(Tags.ClOrdID, o.ClOrdID)
            .Set(Tags.OrigClOrdID, o.OrigClOrdID)
            .Set(Tags.ExecID, report.ExecId)
            .Set(Tags.ExecType, (char)report.ExecType)
            .Set(Tags.OrdStatus, (char)o.Status);
        if (report.RejectReason is { } rej && report.ExecType == ExecType.Rejected)
        {
            er.Set(Tags.OrdRejReason, (int)rej);
        }

        er.Set(Tags.Account, o.Account);
        AppendInstrument(er, o.Contract);
        er.Set(Tags.Side, o.Side == Side.Buy ? '1' : '2')
            .Set(Tags.OrderQty, o.OrderQty)
            .Set(Tags.OrdType, o.OrdType == OrderType.Market ? '1' : '2');
        if (o.Price is { } px)
        {
            er.Set(Tags.Price, px);
        }

        er.Set(Tags.TimeInForce, (char)('0' + (int)o.TimeInForce));
        if (report.ExecType == ExecType.Trade)
        {
            er.Set(Tags.LastQty, report.LastQty).Set(Tags.LastPx, report.LastPx);
        }

        er.Set(Tags.LeavesQty, o.LeavesQty)
            .Set(Tags.CumQty, o.CumQty)
            .Set(Tags.AvgPx, o.AvgPx)
            .SetUtcTimestamp(Tags.TransactTime, report.TransactTime)
            .Set(Tags.Text, report.Text ?? (report.ExecType == ExecType.Rejected ? o.Text : null));

        if (o.Contract.Id != 0 && _marketData.Quote(o.Contract.Id) is { } quote)
        {
            er.Set(Tags.TheoValue, Math.Round((decimal)quote.Theo, 4));
            if (_marketData.Underlying(o.Contract.Underlying) is { } u)
            {
                er.Set(Tags.UnderlyingLastPx, Math.Round((decimal)u.Price, 2));
            }
        }

        session.Send(er);

        if (report.ReceivedTimestamp is { } received && report.ExecType is ExecType.New or ExecType.Rejected)
        {
            _metrics.OrderToAck.RecordSince(received);
        }
    }

    public void OnCancelReject(CancelRejectEvent reject)
    {
        if (!_sessions.TryGetValue(reject.ClientId, out var session))
        {
            return;
        }

        var b = new FixMessageBuilder(MsgTypes.OrderCancelReject);
        b.Set(Tags.OrderID, reject.OrderId ?? "NONE")
            .Set(Tags.ClOrdID, reject.ClOrdID)
            .Set(Tags.OrigClOrdID, reject.OrigClOrdID)
            .Set(Tags.OrdStatus, (char)reject.OrdStatus)
            .SetUtcTimestamp(Tags.TransactTime, reject.TransactTime)
            .Set(Tags.CxlRejResponseTo, reject.ForReplace ? '2' : '1')
            .Set(Tags.CxlRejReason, (int)reject.Reason)
            .Set(Tags.Text, reject.Text);
        session.Send(b);
    }

    private static void AppendInstrument(FixMessageBuilder b, OptionContract c)
    {
        b.Set(Tags.Symbol, c.Underlying);
        if (c.Id == 0)
        {
            return;
        }

        b.Set(Tags.SecurityID, c.OccSymbol)
            .Set(Tags.SecurityIDSource, '8')
            .Set(Tags.SecurityType, "OPT")
            .SetLocalMktDate(Tags.MaturityDate, c.Expiry)
            .Set(Tags.PutOrCall, c.Right == OptionRight.Call ? 1 : 0)
            .Set(Tags.StrikePrice, c.Strike);
    }

    private static void BusinessReject(FixSession session, FixMessage refMsg, BusinessRejectReason reason, string text)
    {
        var b = new FixMessageBuilder(MsgTypes.BusinessMessageReject);
        b.Set(Tags.RefSeqNum, refMsg.MsgSeqNum)
            .Set(Tags.RefMsgType, refMsg.MsgType)
            .Set(Tags.BusinessRejectRefID, refMsg.GetString(Tags.ClOrdID))
            .Set(Tags.BusinessRejectReason, (int)reason)
            .Set(Tags.Text, text);
        session.Send(b);
    }
}
