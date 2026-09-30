using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Tickwire.Fix.Session.Store;
using Tickwire.Fix.Session.Transport;

namespace Tickwire.Fix.Session.Tests;

internal sealed class RecordingObserver : IFixSessionObserver
{
    public ConcurrentQueue<FixWireEvent> Wire { get; } = new();
    public ConcurrentQueue<FixSessionLogEvent> Logs { get; } = new();
    public ConcurrentQueue<FixSessionStateEvent> States { get; } = new();

    public void OnWire(FixWireEvent e) => Wire.Enqueue(e);

    public void OnLog(FixSessionLogEvent e) => Logs.Enqueue(e);

    public void OnState(FixSessionStateEvent e) => States.Enqueue(e);

    public IEnumerable<FixMessage> Sent(string msgType) => Wire
        .Where(w => w.Direction == FixDirection.Outbound && w.Disposition != MessageDisposition.DroppedByFault)
        .Select(w => FixMessage.Parse(w.Raw)).Where(m => m.MsgType == msgType);

    public IEnumerable<FixWireEvent> Outbound(MessageDisposition disposition) =>
        Wire.Where(w => w.Direction == FixDirection.Outbound && w.Disposition == disposition);

    public IEnumerable<FixWireEvent> Inbound(MessageDisposition disposition) =>
        Wire.Where(w => w.Direction == FixDirection.Inbound && w.Disposition == disposition);
}

internal sealed class RecordingApp : NullFixApplication
{
    public ConcurrentQueue<FixMessage> Received { get; } = new();
    public int Logons;
    public int Logouts;

    public override void OnLogon(FixSession session) => Interlocked.Increment(ref Logons);

    public override void OnLogout(FixSession session) => Interlocked.Increment(ref Logouts);

    public override ValueTask OnMessageAsync(FixSession session, FixMessage message, CancellationToken cancellationToken)
    {
        Received.Enqueue(message);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<string?> ClOrdIds => [.. Received.Select(m => m.GetString(Tags.ClOrdID))];
}

/// <summary>An acceptor ("TICKWIRE") and an initiator ("CLIENT") joined by in-memory pipes, on a fake clock.</summary>
internal sealed class SessionHarness : IAsyncDisposable
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2025, 3, 14, 13, 30, 0, TimeSpan.Zero));
    public RecordingObserver AcceptorEvents { get; } = new();
    public RecordingObserver InitiatorEvents { get; } = new();
    public RecordingApp AcceptorApp { get; } = new();
    public RecordingApp InitiatorApp { get; } = new();
    public MemorySessionStore AcceptorStore { get; }
    public MemorySessionStore InitiatorStore { get; }
    public FixSession Acceptor { get; private set; }
    public FixSession Initiator { get; private set; }

    /// <param name="beginString">FIX.4.4, or FIXT.1.1 with <paramref name="acceptorApplVer"/>/<paramref name="initiatorApplVer"/>
    /// as each side's DefaultApplVerID.</param>
    public SessionHarness(int heartBtInt = 30, ISessionStore? acceptorStore = null, string beginString = "FIX.4.4",
        string? acceptorApplVer = null, string? initiatorApplVer = null)
    {
        AcceptorStore = new MemorySessionStore(Time.GetUtcNow().UtcDateTime);
        InitiatorStore = new MemorySessionStore(Time.GetUtcNow().UtcDateTime);
        Acceptor = new FixSession(new SessionSettings
        {
            Id = new SessionId(beginString, "TICKWIRE", "CLIENT"),
            Role = SessionRole.Acceptor,
            Dictionary = Tickwire.Fix.Dictionary.FixDictionary.For(beginString),
            DefaultApplVerID = acceptorApplVer,
        }, acceptorStore ?? AcceptorStore, AcceptorApp, Time, observer: AcceptorEvents);
        Initiator = new FixSession(new SessionSettings
        {
            Id = new SessionId(beginString, "CLIENT", "TICKWIRE"),
            Role = SessionRole.Initiator,
            HeartBtInt = heartBtInt,
            Dictionary = Tickwire.Fix.Dictionary.FixDictionary.For(beginString),
            DefaultApplVerID = initiatorApplVer,
        }, InitiatorStore, InitiatorApp, Time, observer: InitiatorEvents);
        Acceptor.StartAsync();
        Initiator.StartAsync();
    }

    /// <summary>Connects the two sessions. Chaos flags wrap each side's outbound transport with fault injection.</summary>
    public async Task ConnectAsync(bool acceptorChaos = false, bool initiatorChaos = false, bool waitForLogon = true)
    {
        var (acceptorSide, initiatorSide) = PipeTransport.CreatePair("acceptor", "initiator");
        var acceptor = new FixAcceptor(new SingleSessionResolver(Acceptor, acceptorChaos));
        _ = acceptor.AcceptAsync(acceptorSide, CancellationToken.None);
        Initiator.Attach(initiatorSide, null, initiatorChaos);
        if (waitForLogon)
        {
            await Eventually(() => Acceptor.IsLoggedOn && Initiator.IsLoggedOn);
        }
    }

    /// <summary>Moves the fake clock forward one second at a time, letting both loops process each tick.</summary>
    public async Task AdvanceAsync(TimeSpan duration)
    {
        var step = TimeSpan.FromMilliseconds(250);
        for (var t = TimeSpan.Zero; t < duration; t += step)
        {
            Time.Advance(step);
            await SettleAsync();
        }
    }

    public async Task SettleAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await Acceptor.InvokeAsync(() => { });
            await Initiator.InvokeAsync(() => { });
            await Task.Delay(2);
        }
    }

    public static FixMessageBuilder Order(string clOrdId, char side = '1')
    {
        var b = new FixMessageBuilder(MsgTypes.NewOrderSingle);
        b.Set(Tags.ClOrdID, clOrdId).Set(Tags.Symbol, "SPY").Set(Tags.Side, side)
            .SetUtcTimestamp(Tags.TransactTime, new DateTime(2025, 3, 14, 13, 30, 0, DateTimeKind.Utc))
            .Set(Tags.OrderQty, 1m).Set(Tags.OrdType, '1');
        return b;
    }

    public static FixMessageBuilder Report(string clOrdId)
    {
        var b = new FixMessageBuilder(MsgTypes.ExecutionReport);
        b.Set(Tags.OrderID, "O-" + clOrdId).Set(Tags.ClOrdID, clOrdId).Set(Tags.ExecID, "E-" + clOrdId)
            .Set(Tags.ExecType, '0').Set(Tags.OrdStatus, '0').Set(Tags.Symbol, "SPY").Set(Tags.Side, '1')
            .Set(Tags.LeavesQty, 1m).Set(Tags.CumQty, 0m).Set(Tags.AvgPx, 0m);
        return b;
    }

    public static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time");
            }

            await Task.Delay(5);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Initiator.DisposeAsync();
        await Acceptor.DisposeAsync();
    }

    private sealed class SingleSessionResolver(FixSession session, bool chaos) : ISessionResolver
    {
        public ValueTask<SessionResolution> ResolveAsync(FixMessage logon, string remote, CancellationToken cancellationToken) =>
            ValueTask.FromResult(logon.SenderCompID == session.Id.TargetCompID && logon.TargetCompID == session.Id.SenderCompID
                ? new SessionResolution(session, null, chaos)
                : SessionResolution.Reject($"Unknown CompIDs {logon.SenderCompID}->{logon.TargetCompID}"));
    }
}
