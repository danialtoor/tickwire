using FluentAssertions;
using Tickwire.Fix.Session.Store;
using static Tickwire.Fix.Session.Tests.SessionHarness;

namespace Tickwire.Fix.Session.Tests;

public class SessionTests
{
    [Fact]
    public async Task Logon_handshake_activates_both_sides()
    {
        await using var h = new SessionHarness();

        await h.ConnectAsync();

        h.Initiator.NextSenderSeqNum.Should().Be(2);
        h.Acceptor.NextTargetSeqNum.Should().Be(2);
        h.Acceptor.NextSenderSeqNum.Should().Be(2);
        h.Acceptor.HeartBtInt.Should().Be(30);
        h.AcceptorApp.Logons.Should().Be(1);
        h.InitiatorApp.Logons.Should().Be(1);
    }

    [Fact]
    public async Task Application_messages_are_delivered_in_order()
    {
        await using var h = new SessionHarness();
        await h.ConnectAsync();

        foreach (var id in new[] { "A", "B", "C" })
        {
            h.Initiator.Send(Order(id));
        }

        await Eventually(() => h.AcceptorApp.Received.Count == 3);
        h.AcceptorApp.ClOrdIds.Should().Equal("A", "B", "C");
        h.Acceptor.NextTargetSeqNum.Should().Be(5);
    }

    [Fact]
    public async Task Idle_sessions_exchange_heartbeats()
    {
        await using var h = new SessionHarness(heartBtInt: 5);
        await h.ConnectAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(11));

        h.AcceptorEvents.Sent(MsgTypes.Heartbeat).Should().HaveCountGreaterThanOrEqualTo(2);
        h.InitiatorEvents.Sent(MsgTypes.Heartbeat).Should().HaveCountGreaterThanOrEqualTo(2);
        h.AcceptorEvents.Sent(MsgTypes.TestRequest).Should().BeEmpty();
        h.Acceptor.IsLoggedOn.Should().BeTrue();
    }

    [Fact]
    public async Task Silent_peer_gets_TestRequest_then_logout()
    {
        await using var h = new SessionHarness(heartBtInt: 5);
        await h.ConnectAsync();
        h.Initiator.SuppressHeartbeats = true;

        await h.AdvanceAsync(TimeSpan.FromSeconds(6.5));
        h.AcceptorEvents.Sent(MsgTypes.TestRequest).Should().ContainSingle();
        h.Acceptor.IsLoggedOn.Should().BeTrue();

        await h.AdvanceAsync(TimeSpan.FromSeconds(5.5));
        await Eventually(() => h.Acceptor.State == SessionState.Disconnected);
        h.AcceptorEvents.Sent(MsgTypes.Logout).Single().GetString(Tags.Text).Should().Contain("Heartbeat timeout");
    }

    [Fact]
    public async Task TestRequest_is_answered_with_matching_TestReqID()
    {
        await using var h = new SessionHarness(heartBtInt: 5);
        await h.ConnectAsync();

        var tr = new FixMessageBuilder(MsgTypes.TestRequest);
        tr.Set(Tags.TestReqID, "PING-1");
        h.Acceptor.Send(tr);

        await Eventually(() => h.InitiatorEvents.Sent(MsgTypes.Heartbeat).Any(m => m.GetString(Tags.TestReqID) == "PING-1"));
    }

    [Fact]
    public async Task Dropped_messages_are_recovered_by_resend_with_PossDup()
    {
        await using var h = new SessionHarness();
        await h.ConnectAsync(acceptorChaos: true);

        h.Acceptor.Faults.DropNext(2);
        h.Acceptor.Send(Report("R1"));
        h.Acceptor.Send(Report("R2"));
        h.Acceptor.Send(Report("R3"));

        await Eventually(() => h.InitiatorApp.Received.Count == 3);
        h.InitiatorApp.ClOrdIds.Should().Equal("R1", "R2", "R3");
        h.InitiatorApp.Received.Take(2).Should().OnlyContain(m => m.PossDupFlag && m.Has(Tags.OrigSendingTime));

        var rr = h.InitiatorEvents.Sent(MsgTypes.ResendRequest).Single();
        rr.GetInt(Tags.BeginSeqNo).Should().Be(2);
        rr.GetInt(Tags.EndSeqNo).Should().Be(0);
        h.AcceptorEvents.Outbound(MessageDisposition.DroppedByFault).Should().HaveCount(2);
        // EndSeqNo=0 asks for everything from 2 on, so R3 is resent too and the initiator drops that copy as a duplicate.
        h.AcceptorEvents.Outbound(MessageDisposition.Resent).Should().HaveCount(3);
        h.InitiatorEvents.Inbound(MessageDisposition.Queued).Should().ContainSingle();
        await Eventually(() => h.InitiatorEvents.Inbound(MessageDisposition.Duplicate).Count() == 1);
    }

    [Fact]
    public async Task Admin_messages_inside_a_gap_are_gap_filled_not_resent()
    {
        await using var h = new SessionHarness(heartBtInt: 5);
        await h.ConnectAsync(acceptorChaos: true);

        h.Acceptor.Faults.DropNext(1);
        await h.AdvanceAsync(TimeSpan.FromSeconds(5.5)); // acceptor heartbeat is dropped
        h.Acceptor.Send(Report("R1"));

        await Eventually(() => h.InitiatorApp.Received.Count == 1);
        await h.SettleAsync();

        var gapFill = h.AcceptorEvents.Sent(MsgTypes.SequenceReset).Single();
        gapFill.GetBool(Tags.GapFillFlag).Should().BeTrue();
        gapFill.PossDupFlag.Should().BeTrue();
        gapFill.GetInt(Tags.NewSeqNo).Should().Be(gapFill.MsgSeqNum + 1);
        h.InitiatorApp.ClOrdIds.Should().Equal("R1"); // the resent copy of R1 is recognised as a duplicate
        h.InitiatorEvents.Inbound(MessageDisposition.Duplicate).Should().ContainSingle();
        h.Initiator.NextTargetSeqNum.Should().Be(h.Acceptor.NextSenderSeqNum);
    }

    [Fact]
    public async Task Skipped_sequence_numbers_are_gap_filled()
    {
        await using var h = new SessionHarness();
        await h.ConnectAsync();

        h.Initiator.AdjustNextSenderSeqNum(+3);
        h.Initiator.Send(Order("A"));

        await Eventually(() => h.AcceptorApp.Received.Count == 1);
        h.AcceptorEvents.Sent(MsgTypes.ResendRequest).Should().ContainSingle();
        h.InitiatorEvents.Sent(MsgTypes.SequenceReset).Single().GetInt(Tags.NewSeqNo).Should().Be(5);
        h.Acceptor.NextTargetSeqNum.Should().Be(6);
    }

    [Fact]
    public async Task Sequence_number_too_low_causes_logout()
    {
        await using var h = new SessionHarness();
        await h.ConnectAsync();
        h.Initiator.Send(Order("A"));
        await Eventually(() => h.AcceptorApp.Received.Count == 1);

        h.Initiator.AdjustNextSenderSeqNum(-1);
        h.Initiator.Send(Order("B"));

        await Eventually(() => h.Acceptor.State == SessionState.Disconnected && h.Initiator.State == SessionState.Disconnected);
        h.AcceptorEvents.Sent(MsgTypes.Logout).Single().GetString(Tags.Text)
            .Should().Be("MsgSeqNum too low, expecting 3 but received 2");
    }

    [Fact]
    public async Task Garbled_message_is_ignored_and_recovered_through_the_next_gap()
    {
        await using var h = new SessionHarness();
        await h.ConnectAsync(initiatorChaos: true);

        h.Initiator.Faults.CorruptNextChecksum();
        h.Initiator.Send(Order("A"));
        h.Initiator.Send(Order("B"));

        await Eventually(() => h.AcceptorApp.Received.Count == 2);
        h.AcceptorApp.ClOrdIds.Should().Equal("A", "B");
        h.AcceptorEvents.Inbound(MessageDisposition.Garbled).Should().ContainSingle();
        h.AcceptorEvents.Sent(MsgTypes.Reject).Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_field_value_gets_session_Reject_and_session_continues()
    {
        await using var h = new SessionHarness();
        await h.ConnectAsync();

        h.Initiator.Send(Order("BAD", side: 'Z'));
        h.Initiator.Send(Order("GOOD"));

        await Eventually(() => h.AcceptorApp.Received.Count == 1);
        var reject = h.AcceptorEvents.Sent(MsgTypes.Reject).Single();
        reject.GetInt(Tags.RefSeqNum).Should().Be(2);
        reject.GetInt(Tags.RefTagID).Should().Be(Tags.Side);
        reject.GetInt(Tags.SessionRejectReason).Should().Be((int)SessionRejectReason.ValueIsIncorrect);
        reject.GetString(Tags.RefMsgType).Should().Be("D");
        h.AcceptorApp.ClOrdIds.Should().Equal("GOOD");
        h.Acceptor.IsLoggedOn.Should().BeTrue();
    }

    [Fact]
    public async Task Messages_sent_while_offline_are_delivered_after_reconnect()
    {
        await using var h = new SessionHarness();
        await h.ConnectAsync();
        h.Initiator.Logout("end of day");
        await Eventually(() => h.Acceptor.State == SessionState.Disconnected && h.Initiator.State == SessionState.Disconnected);

        h.Acceptor.Send(Report("FILL-WHILE-AWAY"));
        await h.SettleAsync();
        h.AcceptorEvents.Outbound(MessageDisposition.StoredWhileOffline).Should().ContainSingle();

        await h.ConnectAsync();
        await Eventually(() => h.InitiatorApp.Received.Count == 1);
        h.InitiatorApp.Received.Single().PossDupFlag.Should().BeTrue();
    }

    [Fact]
    public async Task Logon_with_reset_flag_restarts_sequence_numbers()
    {
        await using var h = new SessionHarness();
        await h.ConnectAsync();
        h.Initiator.Send(Order("A"));
        await Eventually(() => h.Acceptor.NextTargetSeqNum == 3);
        h.Initiator.Logout();
        await Eventually(() => h.Initiator.State == SessionState.Disconnected);

        h.InitiatorStore.Reset(DateTime.UtcNow);
        // Reconnect the same sessions, this time with ResetSeqNumFlag(141)=Y on the Logon.
        using (var logon = new FixMessageBuilder(MsgTypes.Logon))
        {
            logon.Set(Tags.EncryptMethod, 0).Set(Tags.HeartBtInt, 30).Set(Tags.ResetSeqNumFlag, true);
            var bytes = logon.ToBytes(new FixHeader("FIX.4.4", "CLIENT", "TICKWIRE", 1, h.Time.GetUtcNow().UtcDateTime));
            var (acceptorSide, clientSide) = Transport.PipeTransport.CreatePair();
            h.Acceptor.Attach(acceptorSide, bytes);
            await Eventually(() => h.Acceptor.IsLoggedOn);
            var reply = await clientSide.ReceiveAsync(CancellationToken.None);
            var parsed = FixMessage.Parse(reply!);
            parsed.MsgSeqNum.Should().Be(1);
            parsed.GetBool(Tags.ResetSeqNumFlag).Should().BeTrue();
            await clientSide.DisposeAsync();
        }

        h.Acceptor.NextTargetSeqNum.Should().Be(2);
    }

    [Fact]
    public async Task Sequence_numbers_survive_a_restart_with_a_persistent_store()
    {
        var backend = new InMemoryBackend();
        var id = new SessionId("FIX.4.4", "TICKWIRE", "CLIENT");
        int sender, target;
        {
            await using var store = await WriteBehindSessionStore.OpenAsync(id, backend, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
            await using var h = new SessionHarness(acceptorStore: store);
            await h.ConnectAsync();
            h.Initiator.Send(Order("A"));
            h.Acceptor.Send(Report("A"));
            await Eventually(() => h.AcceptorApp.Received.Count == 1 && h.InitiatorApp.Received.Count == 1);
            await store.FlushAsync();
            sender = h.Acceptor.NextSenderSeqNum;
            target = h.Acceptor.NextTargetSeqNum;
        }

        await using var reopened = await WriteBehindSessionStore.OpenAsync(id, backend, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        reopened.NextSenderSeqNum.Should().Be(sender);
        reopened.NextTargetSeqNum.Should().Be(target);
        reopened.GetOutbound(1, sender).Should().HaveCount(sender - 1);
    }

    private sealed class InMemoryBackend : ISessionStoreBackend
    {
        private readonly Dictionary<SessionId, (int Sender, int Target, DateTime Created, SortedDictionary<int, byte[]> Msgs)> _data = [];

        public Task<StoreSnapshot?> LoadAsync(SessionId session, int maxMessages, CancellationToken cancellationToken)
        {
            lock (_data)
            {
                return Task.FromResult(_data.TryGetValue(session, out var d)
                    ? new StoreSnapshot(d.Sender, d.Target, d.Created, [.. d.Msgs.Select(kv => (kv.Key, kv.Value))])
                    : null);
            }
        }

        public Task ApplyAsync(SessionId session, IReadOnlyList<StoreOp> ops, CancellationToken cancellationToken)
        {
            lock (_data)
            {
                var d = _data.GetValueOrDefault(session, (1, 1, DateTime.UtcNow, new SortedDictionary<int, byte[]>()));
                foreach (var op in ops)
                {
                    switch (op)
                    {
                        case SetSeqNumsOp s:
                            d = (s.NextSenderSeqNum, s.NextTargetSeqNum, d.Item3, d.Item4);
                            break;
                        case AddMessageOp a:
                            d.Item4[a.SeqNum] = a.Message;
                            break;
                        case ResetOp r:
                            d = (1, 1, r.CreationTime, new SortedDictionary<int, byte[]>());
                            break;
                        default:
                            break;
                    }
                }

                _data[session] = d;
            }

            return Task.CompletedTask;
        }
    }
}
