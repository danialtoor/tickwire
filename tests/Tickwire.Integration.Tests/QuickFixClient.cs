using System.Collections.Concurrent;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Logger;
using QuickFix.Store;
using QuickFix.Transport;

namespace Tickwire.Integration.Tests;

/// <summary>A QuickFIX/n initiator: the industry-standard engine acting as a client of the Tickwire acceptor.</summary>
internal sealed class QuickFixClient : IApplication, IDisposable
{
    private readonly SocketInitiator _initiator;

    public QuickFixClient(int port, string sender = "CLIENT", string target = "TICKWIRE", bool resetOnLogon = true,
        string beginString = "FIX.4.4")
    {
        var fixt = beginString == "FIXT.1.1";
        var dictionaries = fixt
            ? $"TransportDataDictionary={Path.Combine(AppContext.BaseDirectory, "FIXT11.xml")}\n"
                + $"AppDataDictionary={Path.Combine(AppContext.BaseDirectory, "FIX50SP2.xml")}\nDefaultApplVerID=FIX.5.0SP2"
            : $"DataDictionary={Path.Combine(AppContext.BaseDirectory, "FIX44.xml")}";
        var config = $"""
            [DEFAULT]
            ConnectionType=initiator
            ReconnectInterval=1
            StartTime=00:00:00
            EndTime=00:00:00
            HeartBtInt=30
            SocketConnectHost=127.0.0.1
            SocketConnectPort={port}
            UseDataDictionary=Y
            {dictionaries}
            ValidateUserDefinedFields=N
            ResetOnLogon={(resetOnLogon ? "Y" : "N")}

            [SESSION]
            BeginString={beginString}
            SenderCompID={sender}
            TargetCompID={target}
            """;
        var settings = new SessionSettings(new StringReader(config));
        SessionId = new SessionID(beginString, sender, target);
        _initiator = new SocketInitiator(this, new MemoryStoreFactory(), settings, new NullLogFactory(),
            new DefaultMessageFactory());
    }

    public SessionID SessionId { get; }
    public ConcurrentQueue<Message> AppMessages { get; } = new();
    public ConcurrentQueue<Message> AdminIn { get; } = new();
    public ConcurrentQueue<Message> AdminOut { get; } = new();
    public volatile bool LoggedOn;

    public Session Session => Session.LookupSession(SessionId)!;

    public void Start() => _initiator.Start();

    public void Send(Message message) => Session.SendToTarget(message, SessionId);

    public static QuickFix.FIX44.NewOrderSingle Order(string clOrdId, decimal qty = 5, decimal price = 2.5m)
    {
        var nos = new QuickFix.FIX44.NewOrderSingle(new ClOrdID(clOrdId), new Symbol("SPY"), new Side(Side.BUY),
            new TransactTime(DateTime.UtcNow), new OrdType(OrdType.LIMIT));
        nos.Set(new OrderQty(qty));
        nos.Set(new Price(price));
        nos.Set(new SecurityType(SecurityType.OPTION));
        nos.Set(new PutOrCall(PutOrCall.CALL));
        nos.Set(new StrikePrice(550m));
        nos.Set(new MaturityDate("20991217"));
        nos.Set(new TimeInForce(TimeInForce.DAY));
        return nos;
    }

    public void Dispose()
    {
        _initiator.Stop(true);
        _initiator.Dispose();
    }

    public void ToAdmin(Message message, SessionID sessionID) => AdminOut.Enqueue(message);

    public void FromAdmin(Message message, SessionID sessionID) => AdminIn.Enqueue(message);

    public void ToApp(Message message, SessionID sessionId)
    {
    }

    public void FromApp(Message message, SessionID sessionID) => AppMessages.Enqueue(message);

    public void OnCreate(SessionID sessionID)
    {
    }

    public void OnLogout(SessionID sessionID) => LoggedOn = false;

    public void OnLogon(SessionID sessionID) => LoggedOn = true;
}
