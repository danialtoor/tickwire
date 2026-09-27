using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Tickwire.Fix;
using Tickwire.Fix.Session;
using Tickwire.Fix.Session.Store;
using Tickwire.Fix.Session.Transport;

namespace Tickwire.Integration.Tests;

/// <summary>
/// Session-layer interop: QuickFIX/n (initiator) against the hand-written Tickwire acceptor over real TCP.
/// The application here is a minimal echo that acknowledges and fills each order.
/// </summary>
public sealed class QuickFixInteropTests : IAsyncLifetime
{
    private readonly ConcurrentQueue<FixWireEvent> _wire = new();
    private FixAcceptor _acceptor = null!;
    private FixSession _session = null!;

    public async Task InitializeAsync()
    {
        var observer = new Observer(_wire);
        _session = new FixSession(new SessionSettings { Id = new SessionId("FIX.4.4", "TICKWIRE", "CLIENT") },
            new MemorySessionStore(DateTime.UtcNow), new AckAndFillApp(), observer: observer);
        await _session.StartAsync();
        _acceptor = new FixAcceptor(new Resolver(_session));
        await _acceptor.StartTcpAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public async Task DisposeAsync()
    {
        await _acceptor.DisposeAsync();
        await _session.DisposeAsync();
    }

    [Fact]
    public async Task QuickFix_logs_on_trades_recovers_a_gap_and_logs_out()
    {
        using var client = new QuickFixClient(_acceptor.Port);
        client.Start();
        await Eventually(() => client.LoggedOn && _session.IsLoggedOn);

        // Order → ack + fill
        client.Send(QuickFixClient.Order("QF-1"));
        await Eventually(() => client.AppMessages.Count == 2);
        client.AppMessages.Select(m => m.GetString(39)).Should().Equal("0", "2");

        // Tickwire → QuickFIX gap: drop our next report. QuickFIX must detect it and ask for a resend.
        _session.Faults.DropNext(1);
        client.Send(QuickFixClient.Order("QF-2"));
        await Eventually(() => client.AppMessages.Count == 4);
        client.AdminOut.Should().Contain(m => m.Header.GetString(35) == "2", "QuickFIX should send a ResendRequest");
        client.AppMessages.Skip(2).Select(m => m.GetString(11)).Should().Equal("QF-2", "QF-2");
        _wire.Should().Contain(w => w.Disposition == MessageDisposition.Resent);

        // QuickFIX → Tickwire gap: skip QuickFIX's outbound sequence numbers. Tickwire must ask for a resend and accept the gap fill.
        client.Session.NextSenderMsgSeqNum += 2;
        client.Send(QuickFixClient.Order("QF-3"));
        await Eventually(() => client.AppMessages.Count == 6);
        _wire.Should().Contain(w => w.Direction == FixDirection.Outbound && FixMessage.Parse(w.Raw).MsgType == "2");
        _wire.Should().Contain(w => w.Direction == FixDirection.Inbound && FixMessage.Parse(w.Raw).MsgType == "4");
        (client.Session.NextSenderMsgSeqNum).Should().Be((ulong)_session.NextTargetSeqNum);

        // Clean logout
        client.Session.Logout("done");
        await Eventually(() => _session.State == SessionState.Disconnected);
        client.AdminIn.Should().Contain(m => m.Header.GetString(35) == "5");
    }

    [Fact]
    public async Task Unknown_CompIDs_get_a_Logout_with_a_reason()
    {
        using var client = new QuickFixClient(_acceptor.Port, sender: "STRANGER");
        client.Start();

        await Eventually(() => client.AdminIn.Any(m => m.Header.GetString(35) == "5"));
        client.AdminIn.First(m => m.Header.GetString(35) == "5").GetString(58).Should().Contain("Unknown");
        _session.State.Should().Be(SessionState.Disconnected);
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time");
            }

            await Task.Delay(20);
        }
    }

    private sealed class Resolver(FixSession session) : ISessionResolver
    {
        public ValueTask<SessionResolution> ResolveAsync(FixMessage logon, string remote, CancellationToken cancellationToken) =>
            ValueTask.FromResult(logon.SenderCompID == "CLIENT" && logon.TargetCompID == "TICKWIRE"
                ? new SessionResolution(session, null, EnableChaos: true)
                : SessionResolution.Reject($"Unknown session {logon.SenderCompID}->{logon.TargetCompID}"));
    }

    private sealed class Observer(ConcurrentQueue<FixWireEvent> wire) : IFixSessionObserver
    {
        public void OnWire(FixWireEvent e) => wire.Enqueue(e);

        public void OnLog(FixSessionLogEvent e)
        {
        }

        public void OnState(FixSessionStateEvent e)
        {
        }
    }

    private sealed class AckAndFillApp : NullFixApplication
    {
        private int _ids;

        public override ValueTask OnMessageAsync(FixSession session, FixMessage message, CancellationToken cancellationToken)
        {
            if (message.MsgType != MsgTypes.NewOrderSingle)
            {
                return ValueTask.CompletedTask;
            }

            var qty = message.GetDecimal(Tags.OrderQty) ?? 0;
            var px = message.GetDecimal(Tags.Price) ?? 0;
            session.Send(Report(message, '0', '0', 0, qty, 0, 0));
            session.Send(Report(message, 'F', '2', qty, 0, qty, px));
            return ValueTask.CompletedTask;
        }

        private FixMessageBuilder Report(FixMessage order, char execType, char ordStatus, decimal cum, decimal leaves,
            decimal lastQty, decimal lastPx)
        {
            var id = Interlocked.Increment(ref _ids);
            var b = new FixMessageBuilder(MsgTypes.ExecutionReport);
            b.Set(Tags.OrderID, "T" + order.GetString(Tags.ClOrdID)).Set(Tags.ClOrdID, order.GetString(Tags.ClOrdID))
                .Set(Tags.ExecID, "E" + id).Set(Tags.ExecType, execType).Set(Tags.OrdStatus, ordStatus)
                .Set(Tags.Symbol, "SPY").Set(Tags.Side, order.GetChar(Tags.Side) ?? '1').Set(Tags.OrderQty, cum + leaves)
                .Set(Tags.LeavesQty, leaves).Set(Tags.CumQty, cum).Set(Tags.AvgPx, lastPx);
            if (lastQty > 0)
            {
                b.Set(Tags.LastQty, lastQty).Set(Tags.LastPx, lastPx);
            }

            return b;
        }
    }
}
