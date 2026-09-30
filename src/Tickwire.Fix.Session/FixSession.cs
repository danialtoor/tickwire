using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Tickwire.Fix.Session.Store;
using Tickwire.Fix.Session.Transport;

namespace Tickwire.Fix.Session;

/// <summary>
/// One FIX session: sequence numbers, logon/logout, heartbeats, gap detection and recovery.
/// <para>
/// Everything runs on a single loop fed by a channel: inbound messages, outbound sends, timer ticks and admin commands.
/// State is only touched from that loop, so there are no locks. Callers interact through <see cref="Send"/> and the
/// command methods, which enqueue work and return immediately.
/// </para>
/// </summary>
public sealed partial class FixSession : IAsyncDisposable
{
    private readonly SessionSettings _settings;
    private readonly ISessionStore _store;
    private readonly IFixApplication _app;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IFixSessionObserver? _observer;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SortedDictionary<int, FixMessage> _queued = [];
    private readonly HashSet<int> _serviced = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _ticker;
    private Task? _loop;

    private IFixTransport? _transport;
    private int _generation;
    private int _heartBtInt;
    private DateTime _lastSent;
    private DateTime _lastReceived;
    private DateTime _stateEnteredAt;
    private string? _pendingTestReqId;
    private DateTime _testRequestSentAt;
    private int _testReqCounter;
    private int _resendUpTo; // while > 0 we have an outstanding ResendRequest covering seqs up to this number
    private bool _sentResetOnLogon;

    public FixSession(SessionSettings settings, ISessionStore store, IFixApplication app, TimeProvider? time = null,
        ILogger? logger = null, IFixSessionObserver? observer = null)
    {
        _settings = settings;
        _store = store;
        _app = app;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _observer = observer;
        _heartBtInt = settings.HeartBtInt;
        _ticker = _time.CreateTimer(_ => _commands.Writer.TryWrite(new TickCommand()), null, TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(250));
    }

    public SessionId Id => _settings.Id;
    public SessionSettings Settings => _settings;
    public SessionState State { get; private set; } = SessionState.Disconnected;
    public bool IsLoggedOn => State == SessionState.Active;
    public int HeartBtInt => _heartBtInt;
    public int NextSenderSeqNum => _store.NextSenderSeqNum;
    public int NextTargetSeqNum => _store.NextTargetSeqNum;
    public string? RemoteDescription => _transport?.Description;
    public DateTime? LastReceivedAt => _lastReceived == default ? null : _lastReceived;

    /// <summary>Outbound faults for the Chaos panel. Only applied when the session was attached with chaos enabled.</summary>
    public ChaosFaults Faults { get; } = new();

    /// <summary>Chaos: stop sending heartbeats and stop answering TestRequests (simulates a hung counterparty).</summary>
    public bool SuppressHeartbeats { get; set; }

    /// <summary>Initiator: send ResetSeqNumFlag(141)=Y on the next Logon only (e.g. after losing local state).</summary>
    public bool ResetNextLogon { get; set; }

    /// <summary>Free-form data the host can hang on the session (client id, risk profile, ...).</summary>
    public object? Tag { get; set; }

    /// <summary>Raised on the session loop when the session disconnects, with the reason.</summary>
    public event Action<FixSession, string>? Disconnected;

    public Task StartAsync()
    {
        _loop ??= Task.Run(RunLoopAsync);
        return Task.CompletedTask;
    }

    /// <summary>Queues an application message. It gets the next sequence number and is stored for resend.</summary>
    public bool Send(FixMessageBuilder message) => _commands.Writer.TryWrite(new SendCommand(message));

    /// <summary>
    /// Binds a connection. An acceptor passes the Logon it already read (it needed it to find this session);
    /// an initiator passes null and the session sends Logon itself.
    /// </summary>
    public void Attach(IFixTransport transport, byte[]? firstInbound = null, bool enableChaos = false) =>
        _commands.Writer.TryWrite(new AttachCommand(enableChaos ? new FaultInjectingTransport(transport, Faults) : transport,
            firstInbound));

    public void Logout(string? text = null) => _commands.Writer.TryWrite(new LogoutCommand(text));

    public void Disconnect(string reason) => _commands.Writer.TryWrite(new DisconnectCommand(reason, -1));

    /// <summary>Resets both sequence numbers to 1. If logged on, logs out first.</summary>
    public void ResetSequenceNumbers() => _commands.Writer.TryWrite(new ResetCommand());

    /// <summary>
    /// Chaos/ops: move our outbound sequence number. Positive skips numbers (the peer sees a gap, we answer with a
    /// GapFill). Negative repeats numbers (the peer sees "MsgSeqNum too low" and logs us out).
    /// </summary>
    public void AdjustNextSenderSeqNum(int delta) => _commands.Writer.TryWrite(new InvokeCommand(() =>
    {
        var next = Math.Max(1, _store.NextSenderSeqNum + delta);
        Log(SessionLogLevel.Warning, $"Outbound MsgSeqNum moved from {_store.NextSenderSeqNum} to {next} (manual adjustment)");
        _store.NextSenderSeqNum = next;
        return ValueTask.CompletedTask;
    }));

    /// <summary>Chaos/tests: send an arbitrary message body with the next sequence number, bypassing nothing.</summary>
    public void SendRaw(FixMessageBuilder message) => Send(message);

    /// <summary>Runs <paramref name="action"/> on the session loop, after everything queued before it.</summary>
    public Task InvokeAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Writer.TryWrite(new InvokeCommand(() =>
        {
            action();
            done.TrySetResult();
            return ValueTask.CompletedTask;
        }));
        return done.Task;
    }

    private int _disposeState1;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState1, 1) != 0)
        {
            return;
        }

        await _ticker.DisposeAsync().ConfigureAwait(false);
        _commands.Writer.TryComplete();
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

        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task RunLoopAsync()
    {
        var reader = _commands.Reader;
        while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
        {
            while (reader.TryRead(out var command))
            {
                try
                {
                    await HandleAsync(command).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SessionLog.LoopError(_logger, ex, Id.ToString());
                    Log(SessionLogLevel.Error, $"Internal error: {ex.Message}");
                }
            }
        }
    }

    private ValueTask HandleAsync(Command command) => command switch
    {
        InboundCommand c when c.Generation == _generation => OnInboundAsync(c.Bytes),
        InboundCommand => ValueTask.CompletedTask, // from a connection we already closed
        SendCommand c => SendApplicationAsync(c.Message),
        TickCommand => OnTickAsync(),
        AttachCommand c => OnAttachAsync(c.Transport, c.FirstInbound),
        LogoutCommand c => InitiateLogoutAsync(c.Text),
        DisconnectCommand c when c.Generation == -1 || c.Generation == _generation => DisconnectAsync(c.Reason),
        DisconnectCommand => ValueTask.CompletedTask,
        ResetCommand => OnResetAsync(),
        InvokeCommand c => c.Action(),
        _ => ValueTask.CompletedTask,
    };

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ---------------------------------------------------------------- connection lifecycle

    private async ValueTask OnAttachAsync(IFixTransport transport, byte[]? firstInbound)
    {
        if (_transport is not null)
        {
            await DisconnectAsync("Replaced by a new connection").ConfigureAwait(false);
        }

        _transport = transport;
        var generation = ++_generation;
        _lastReceived = Now;
        _lastSent = Now;
        _queued.Clear();
        _serviced.Clear();
        _resendUpTo = 0;
        _pendingTestReqId = null;
        SetState(SessionState.AwaitingLogon, $"Connected ({transport.Description})");
        _ = Task.Run(() => ReadLoopAsync(transport, generation));

        if (_settings.Role == SessionRole.Initiator)
        {
            await SendLogonAsync(null).ConfigureAwait(false);
        }
        else if (firstInbound is not null)
        {
            await OnInboundAsync(firstInbound).ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(IFixTransport transport, int generation)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await transport.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                var discarded = transport.TakeDiscardedByteCount();
                if (discarded > 0)
                {
                    _commands.Writer.TryWrite(new InvokeCommand(() =>
                    {
                        Log(SessionLogLevel.Warning, $"Discarded {discarded} bytes that were not part of a FIX message");
                        return ValueTask.CompletedTask;
                    }));
                }

                if (message is null)
                {
                    break;
                }

                _commands.Writer.TryWrite(new InboundCommand(message, generation));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _commands.Writer.TryWrite(new DisconnectCommand("Connection closed by peer", generation));
    }

    private async ValueTask DisconnectAsync(string reason)
    {
        var transport = _transport;
        if (transport is null && State == SessionState.Disconnected)
        {
            return;
        }

        _transport = null;
        _generation++;
        var wasLoggedOn = State is SessionState.Active or SessionState.AwaitingLogoutAck;
        _queued.Clear();
        _resendUpTo = 0;
        _pendingTestReqId = null;
        SetState(SessionState.Disconnected, reason);
        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        if (wasLoggedOn)
        {
            _app.OnLogout(this);
        }

        Disconnected?.Invoke(this, reason);
    }

    private async ValueTask InitiateLogoutAsync(string? text)
    {
        if (State != SessionState.Active)
        {
            await DisconnectAsync(text ?? "Logout requested").ConfigureAwait(false);
            return;
        }

        await SendLogoutAsync(text).ConfigureAwait(false);
        SetState(SessionState.AwaitingLogoutAck, text ?? "Logout sent");
    }

    private async ValueTask OnResetAsync()
    {
        if (State != SessionState.Disconnected)
        {
            await InitiateLogoutAsync("Sequence reset by operator").ConfigureAwait(false);
            await DisconnectAsync("Sequence reset by operator").ConfigureAwait(false);
        }

        _store.Reset(Now);
        Log(SessionLogLevel.Warning, "Sequence numbers reset to 1 by operator");
    }

    // ---------------------------------------------------------------- timers

    private async ValueTask OnTickAsync()
    {
        var now = Now;
        var elapsedInState = now - _stateEnteredAt;
        switch (State)
        {
            case SessionState.AwaitingLogon when elapsedInState > _settings.LogonTimeout:
                await DisconnectAsync("Logon timeout").ConfigureAwait(false);
                return;
            case SessionState.AwaitingLogoutAck when elapsedInState > _settings.LogoutTimeout:
                await DisconnectAsync("Logout acknowledgement timeout").ConfigureAwait(false);
                return;
            case SessionState.Active:
                break;
            default:
                return;
        }

        var interval = TimeSpan.FromSeconds(_heartBtInt);

        if (_pendingTestReqId is not null)
        {
            if (now - _testRequestSentAt >= interval)
            {
                Log(SessionLogLevel.Error, $"No response to TestRequest {_pendingTestReqId} within {_heartBtInt}s");
                await SendLogoutAsync("Heartbeat timeout: no response to TestRequest").ConfigureAwait(false);
                await DisconnectAsync("Heartbeat timeout").ConfigureAwait(false);
                return;
            }
        }
        else if (now - _lastReceived >= interval * 1.2)
        {
            // Nothing heard for longer than the heartbeat interval plus a 20% transmission allowance.
            _pendingTestReqId = $"TEST-{++_testReqCounter}";
            _testRequestSentAt = now;
            Log(SessionLogLevel.Warning, $"No messages for {(now - _lastReceived).TotalSeconds:0.#}s, sending TestRequest {_pendingTestReqId}");
            using var tr = new FixMessageBuilder(MsgTypes.TestRequest);
            tr.Set(Tags.TestReqID, _pendingTestReqId);
            await SendAdminAsync(tr).ConfigureAwait(false);
        }

        if (!SuppressHeartbeats && now - _lastSent >= interval)
        {
            using var hb = new FixMessageBuilder(MsgTypes.Heartbeat);
            await SendAdminAsync(hb).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- inbound

    private async ValueTask OnInboundAsync(byte[] bytes)
    {
        _lastReceived = Now;

        if (!FixParser.TryParse(bytes, out var msg, out var error))
        {
            Wire(FixDirection.Inbound, bytes, MessageDisposition.Garbled, $"Unparseable: {error}");
            Log(SessionLogLevel.Warning, $"Ignored garbled message ({error})");
            return;
        }

        if (!msg.IsIntact)
        {
            var why = msg.Integrity.HasFlag(FixIntegrity.ChecksumMismatch)
                ? $"CheckSum {msg.DeclaredChecksum:000} but computed {msg.ActualChecksum:000}"
                : $"BodyLength {msg.DeclaredBodyLength} but actual {msg.ActualBodyLength}";
            Wire(FixDirection.Inbound, bytes, MessageDisposition.Garbled, why);
            Log(SessionLogLevel.Warning, $"Ignored garbled message: {why}");
            return;
        }

        if (msg.BeginString != _settings.Id.BeginString)
        {
            Wire(FixDirection.Inbound, bytes, MessageDisposition.Rejected, "Wrong BeginString");
            await SendLogoutAsync($"Incorrect BeginString {msg.BeginString}").ConfigureAwait(false);
            await DisconnectAsync("Incorrect BeginString").ConfigureAwait(false);
            return;
        }

        if (msg.SenderCompID != _settings.Id.TargetCompID || msg.TargetCompID != _settings.Id.SenderCompID)
        {
            Wire(FixDirection.Inbound, bytes, MessageDisposition.Rejected, "CompID problem");
            await SendRejectAsync(msg, SessionRejectReason.CompIdProblem, Tags.SenderCompID, "CompID problem").ConfigureAwait(false);
            await SendLogoutAsync("CompID problem").ConfigureAwait(false);
            await DisconnectAsync("CompID problem").ConfigureAwait(false);
            return;
        }

        if (State == SessionState.AwaitingLogon && msg.MsgType != MsgTypes.Logon)
        {
            Wire(FixDirection.Inbound, bytes, MessageDisposition.Rejected, "First message must be Logon");
            await DisconnectAsync($"Received {msg.MsgType} before Logon").ConfigureAwait(false);
            return;
        }

        switch (msg.MsgType)
        {
            case MsgTypes.Logon:
                await OnLogonAsync(msg).ConfigureAwait(false);
                return;
            case MsgTypes.SequenceReset when msg.GetBool(Tags.GapFillFlag) != true:
                Wire(FixDirection.Inbound, bytes, MessageDisposition.Normal);
                await OnSequenceResetResetAsync(msg).ConfigureAwait(false);
                return;
            default:
                break;
        }

        await ProcessInSequenceAsync(msg).ConfigureAwait(false);
    }

    /// <summary>Applies the sequence check, handles the message if it is next, then drains any queued messages.</summary>
    private async ValueTask ProcessInSequenceAsync(FixMessage msg)
    {
        if (!await CheckSequenceAsync(msg).ConfigureAwait(false))
        {
            return;
        }

        Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Normal, msg.PossDupFlag ? "PossDup" : null);
        await DispatchAndAdvanceAsync(msg).ConfigureAwait(false);
        await DrainQueueAsync().ConfigureAwait(false);
    }

    private async ValueTask DrainQueueAsync()
    {
        while (_transport is not null && _queued.Remove(_store.NextTargetSeqNum, out var next))
        {
            Wire(FixDirection.Inbound, next.Raw.ToArray(), MessageDisposition.Normal, "Released from queue after gap fill");
            await DispatchAndAdvanceAsync(next).ConfigureAwait(false);
        }

        // Anything left below the expected number was covered by a gap fill.
        while (_queued.Count > 0 && _queued.Keys.First() < _store.NextTargetSeqNum)
        {
            _queued.Remove(_queued.Keys.First());
        }

        if (_resendUpTo > 0 && _store.NextTargetSeqNum > _resendUpTo)
        {
            Log(SessionLogLevel.Info, $"Gap recovered; in sequence again at {_store.NextTargetSeqNum}");
            _resendUpTo = 0;
        }
    }

    /// <returns>True when the message is the next expected one and should be processed now.</returns>
    private async ValueTask<bool> CheckSequenceAsync(FixMessage msg)
    {
        var seq = msg.MsgSeqNum;
        var expected = _store.NextTargetSeqNum;

        if (seq > expected)
        {
            Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Queued, $"Expected {expected}, got {seq}");
            if (msg.MsgType == MsgTypes.ResendRequest && _serviced.Add(seq))
            {
                // Service resend requests right away even when we have our own gap, or both sides would wait forever.
                await OnResendRequestAsync(msg).ConfigureAwait(false);
            }

            if (msg.MsgType == MsgTypes.Logout)
            {
                await OnLogoutAsync(msg).ConfigureAwait(false);
                return false;
            }

            _queued[seq] = msg;
            await RequestResendAsync(expected, seq).ConfigureAwait(false);
            return false;
        }

        if (seq < expected)
        {
            if (msg.PossDupFlag)
            {
                if (!msg.Has(Tags.OrigSendingTime) && msg.MsgType != MsgTypes.SequenceReset)
                {
                    Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Rejected, "PossDup without OrigSendingTime");
                    await SendRejectAsync(msg, SessionRejectReason.RequiredTagMissing, Tags.OrigSendingTime,
                        "Required tag missing: OrigSendingTime(122)").ConfigureAwait(false);
                    return false;
                }

                Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Duplicate, $"Already processed {seq}");
                return false;
            }

            var text = $"MsgSeqNum too low, expecting {expected} but received {seq}";
            Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Rejected, text);
            Log(SessionLogLevel.Error, text);
            await SendLogoutAsync(text).ConfigureAwait(false);
            await DisconnectAsync(text).ConfigureAwait(false);
            return false;
        }

        if (msg.PossDupFlag && msg.MsgType != MsgTypes.SequenceReset)
        {
            var orig = msg.GetUtcTimestamp(Tags.OrigSendingTime);
            var sending = msg.GetUtcTimestamp(Tags.SendingTime);
            if (orig is null)
            {
                Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Rejected, "PossDup without OrigSendingTime");
                await SendRejectAsync(msg, SessionRejectReason.RequiredTagMissing, Tags.OrigSendingTime,
                    "Required tag missing: OrigSendingTime(122)").ConfigureAwait(false);
                _store.NextTargetSeqNum = seq + 1;
                return false;
            }

            if (sending is not null && orig > sending)
            {
                Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Rejected, "OrigSendingTime after SendingTime");
                await SendRejectAsync(msg, SessionRejectReason.SendingTimeAccuracyProblem, Tags.OrigSendingTime,
                    "OrigSendingTime is later than SendingTime").ConfigureAwait(false);
                await SendLogoutAsync("SendingTime accuracy problem").ConfigureAwait(false);
                await DisconnectAsync("SendingTime accuracy problem").ConfigureAwait(false);
                return false;
            }
        }
        else if (_settings.MaxLatency > TimeSpan.Zero)
        {
            var sending = msg.GetUtcTimestamp(Tags.SendingTime);
            if (sending is null || (Now - sending.Value).Duration() > _settings.MaxLatency)
            {
                Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Rejected, "SendingTime accuracy problem");
                await SendRejectAsync(msg, SessionRejectReason.SendingTimeAccuracyProblem, Tags.SendingTime,
                    $"SendingTime more than {_settings.MaxLatency.TotalSeconds:0}s from server time").ConfigureAwait(false);
                await SendLogoutAsync("SendingTime accuracy problem").ConfigureAwait(false);
                await DisconnectAsync("SendingTime accuracy problem").ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }

    private async ValueTask DispatchAndAdvanceAsync(FixMessage msg)
    {
        var seq = msg.MsgSeqNum;
        switch (msg.MsgType)
        {
            case MsgTypes.Heartbeat:
                if (_pendingTestReqId is not null && msg.GetString(Tags.TestReqID) == _pendingTestReqId)
                {
                    Log(SessionLogLevel.Info, $"TestRequest {_pendingTestReqId} answered");
                    _pendingTestReqId = null;
                }

                break;

            case MsgTypes.TestRequest:
                if (!SuppressHeartbeats)
                {
                    using var hb = new FixMessageBuilder(MsgTypes.Heartbeat);
                    hb.Set(Tags.TestReqID, msg.GetString(Tags.TestReqID));
                    await SendAdminAsync(hb).ConfigureAwait(false);
                }

                break;

            case MsgTypes.ResendRequest:
                if (_serviced.Remove(seq))
                {
                    break; // already answered when it first arrived out of order
                }

                await OnResendRequestAsync(msg).ConfigureAwait(false);
                break;

            case MsgTypes.Reject:
                Log(SessionLogLevel.Warning,
                    $"Peer rejected our message {msg.GetInt(Tags.RefSeqNum)}: {msg.GetString(Tags.Text) ?? "(no text)"}"
                    + (msg.GetInt(Tags.SessionRejectReason) is { } r ? $" [373={r}]" : string.Empty));
                break;

            case MsgTypes.SequenceReset:
                // Gap fill (123=Y). Reset mode is handled before the sequence check.
                var newSeq = msg.GetInt(Tags.NewSeqNo) ?? 0;
                if (newSeq <= seq)
                {
                    await SendRejectAsync(msg, SessionRejectReason.ValueIsIncorrect, Tags.NewSeqNo,
                        $"NewSeqNo {newSeq} must be greater than MsgSeqNum {seq}").ConfigureAwait(false);
                    break;
                }

                Log(SessionLogLevel.Info, $"GapFill: sequence {seq}..{newSeq - 1} skipped by peer, next expected {newSeq}");
                _store.NextTargetSeqNum = newSeq;
                return;

            case MsgTypes.Logout:
                _store.NextTargetSeqNum = seq + 1;
                await OnLogoutAsync(msg).ConfigureAwait(false);
                return;

            case MsgTypes.Logon:
                Log(SessionLogLevel.Warning, "Ignored Logon received while already logged on");
                break;

            default:
                await DispatchApplicationAsync(msg).ConfigureAwait(false);
                break;
        }

        // A message rejected at the session level still consumes its sequence number.
        if (_store.NextTargetSeqNum == seq)
        {
            _store.NextTargetSeqNum = seq + 1;
        }
    }

    private async ValueTask DispatchApplicationAsync(FixMessage msg)
    {
        if (State != SessionState.Active)
        {
            Log(SessionLogLevel.Warning, $"Ignored {msg.MsgType} while {State}");
            return;
        }

        // FIXT: a message may name its own application version in ApplVerID(1128); we only speak the session's.
        if (_settings.DefaultApplVerID is { } applVerId && msg.GetString(Tags.ApplVerID) is { } own && own != applVerId)
        {
            var text = $"ApplVerID(1128)={own} is not supported on this session; use {applVerId} or omit it";
            Log(SessionLogLevel.Warning, $"Rejected {msg.MsgSeqNum}: {text}");
            await SendRejectAsync(msg, SessionRejectReason.ValueIsIncorrect, Tags.ApplVerID, text).ConfigureAwait(false);
            return;
        }

        if (_settings.ValidateMessages && _settings.Dictionary.Validate(msg) is { } issue)
        {
            Log(SessionLogLevel.Warning, $"Rejected {msg.MsgSeqNum}: {issue.Text}");
            await SendRejectAsync(msg, issue.Reason, issue.RefTagId, issue.Text).ConfigureAwait(false);
            return;
        }

        // Advance before calling out, so replies the application sends synchronously see a consistent state.
        _store.NextTargetSeqNum = msg.MsgSeqNum + 1;
        await _app.OnMessageAsync(this, msg, _stop.Token).ConfigureAwait(false);
    }

    private async ValueTask OnLogonAsync(FixMessage msg)
    {
        if (State == SessionState.Active)
        {
            await ProcessInSequenceAsync(msg).ConfigureAwait(false);
            return;
        }

        if (State != SessionState.AwaitingLogon)
        {
            return;
        }

        var reset = msg.GetBool(Tags.ResetSeqNumFlag) == true;
        var heartBtInt = msg.GetInt(Tags.HeartBtInt);
        if (heartBtInt is null || heartBtInt < _settings.MinHeartBtInt || heartBtInt > _settings.MaxHeartBtInt)
        {
            Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Rejected, "Invalid HeartBtInt");
            await SendLogoutAsync($"HeartBtInt must be between {_settings.MinHeartBtInt} and {_settings.MaxHeartBtInt}")
                .ConfigureAwait(false);
            await DisconnectAsync("Invalid HeartBtInt on Logon").ConfigureAwait(false);
            return;
        }

        if (_settings.DefaultApplVerID is { } expectedVer && msg.GetString(Tags.DefaultApplVerID) != expectedVer)
        {
            var got = msg.GetString(Tags.DefaultApplVerID);
            Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Rejected, "Unsupported DefaultApplVerID");
            await SendLogoutAsync(got is null
                ? $"DefaultApplVerID(1137) is required on a FIXT.1.1 Logon; use {expectedVer} (FIX.5.0SP2)"
                : $"DefaultApplVerID(1137)={got} is not supported; use {expectedVer} (FIX.5.0SP2)").ConfigureAwait(false);
            await DisconnectAsync("Unsupported DefaultApplVerID").ConfigureAwait(false);
            return;
        }

        if ((msg.GetInt(Tags.EncryptMethod) ?? 0) != 0)
        {
            Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Rejected, "Unsupported EncryptMethod");
            await SendLogoutAsync("Unsupported EncryptMethod, use 98=0").ConfigureAwait(false);
            await DisconnectAsync("Unsupported EncryptMethod").ConfigureAwait(false);
            return;
        }

        if (_settings.Role == SessionRole.Acceptor)
        {
            if (reset)
            {
                Log(SessionLogLevel.Info, "Logon with ResetSeqNumFlag=Y: sequence numbers reset to 1");
                _store.Reset(Now);
            }

            _heartBtInt = heartBtInt.Value;
        }
        else if (_sentResetOnLogon && !reset)
        {
            Log(SessionLogLevel.Warning, "Peer did not confirm ResetSeqNumFlag");
        }

        var seq = msg.MsgSeqNum;
        var expected = _store.NextTargetSeqNum;
        if (seq < expected)
        {
            var text = $"MsgSeqNum too low, expecting {expected} but received {seq}";
            Wire(FixDirection.Inbound, msg.Raw.ToArray(), MessageDisposition.Rejected, text);
            await SendLogoutAsync(text).ConfigureAwait(false);
            await DisconnectAsync(text).ConfigureAwait(false);
            return;
        }

        Wire(FixDirection.Inbound, msg.Raw.ToArray(), seq > expected ? MessageDisposition.Queued : MessageDisposition.Normal,
            seq > expected ? $"Expected {expected}, got {seq}" : null);

        if (_settings.Role == SessionRole.Acceptor)
        {
            await SendLogonAsync(reset).ConfigureAwait(false);
        }

        SetState(SessionState.Active, "Logged on");
        Log(SessionLogLevel.Info, $"Logged on. HeartBtInt={_heartBtInt}s, next outbound {_store.NextSenderSeqNum}, next expected {seq + (seq == expected ? 1 : 0)}");
        _app.OnLogon(this);

        if (seq > expected)
        {
            // The Logon itself is part of the gap; the peer's gap fill will cover it.
            await RequestResendAsync(expected, seq).ConfigureAwait(false);
        }
        else
        {
            _store.NextTargetSeqNum = seq + 1;
        }
    }

    private async ValueTask OnSequenceResetResetAsync(FixMessage msg)
    {
        var newSeq = msg.GetInt(Tags.NewSeqNo) ?? 0;
        var expected = _store.NextTargetSeqNum;
        if (newSeq < expected)
        {
            await SendRejectAsync(msg, SessionRejectReason.ValueIsIncorrect, Tags.NewSeqNo,
                $"Attempt to lower sequence number from {expected} to {newSeq}").ConfigureAwait(false);
            return;
        }

        Log(SessionLogLevel.Warning, $"SequenceReset-Reset: next expected moved from {expected} to {newSeq}");
        _store.NextTargetSeqNum = newSeq;
        _queued.Clear();
        _resendUpTo = 0;
    }

    private async ValueTask OnLogoutAsync(FixMessage msg)
    {
        var text = msg.GetString(Tags.Text);
        if (State == SessionState.AwaitingLogoutAck)
        {
            Log(SessionLogLevel.Info, "Logout confirmed by peer");
        }
        else
        {
            Log(SessionLogLevel.Info, $"Peer logged out{(text is null ? string.Empty : $": {text}")}");
            await SendLogoutAsync(null).ConfigureAwait(false);
        }

        await DisconnectAsync(text is null ? "Logged out" : $"Logged out: {text}").ConfigureAwait(false);
        if (_settings.ResetOnLogout)
        {
            _store.Reset(Now);
        }
    }

    private async ValueTask OnResendRequestAsync(FixMessage msg)
    {
        var begin = msg.GetInt(Tags.BeginSeqNo) ?? 1;
        var end = msg.GetInt(Tags.EndSeqNo) ?? 0;
        var lastSent = _store.NextSenderSeqNum - 1;
        if (end == 0 || end > lastSent)
        {
            end = lastSent;
        }

        Log(SessionLogLevel.Warning, $"ResendRequest received for {begin}..{(msg.GetInt(Tags.EndSeqNo) is 0 or null ? "∞" : end.ToString(CultureInfo.InvariantCulture))}; replaying {begin}..{end}");
        if (begin > end)
        {
            return;
        }

        var stored = _store.GetOutbound(begin, end);
        var gapStart = begin;
        var resent = 0;
        foreach (var (seq, bytes) in stored)
        {
            if (!FixParser.TryParse(bytes, out var original, out _) || original.IsAdmin)
            {
                continue; // admin messages are never resent; they fall into the gap fill below
            }

            if (seq > gapStart)
            {
                await SendGapFillAsync(gapStart, seq).ConfigureAwait(false);
            }

            using var body = FixMessageBuilder.FromBody(original);
            var header = new FixHeader(_settings.Id.BeginString, _settings.Id.SenderCompID, _settings.Id.TargetCompID, seq, Now,
                PossDupFlag: true, OrigSendingTime: original.GetUtcTimestamp(Tags.SendingTime) ?? Now);
            await TransmitAsync(body.ToBytes(header), MessageDisposition.Resent, $"Resend of {seq}").ConfigureAwait(false);
            resent++;
            gapStart = seq + 1;
        }

        if (gapStart <= end)
        {
            await SendGapFillAsync(gapStart, end + 1).ConfigureAwait(false);
        }

        Log(SessionLogLevel.Info, $"Resend complete: {resent} application message(s) resent, admin messages gap-filled");
    }

    private async ValueTask RequestResendAsync(int from, int received)
    {
        if (_resendUpTo > 0 && received <= _resendUpTo)
        {
            return; // an outstanding request (EndSeqNo=0 means "everything") already covers this
        }

        if (_resendUpTo > 0)
        {
            // A request is already open with EndSeqNo=0; it covers everything the peer sent, including this.
            _resendUpTo = received;
            return;
        }

        _resendUpTo = received;
        Log(SessionLogLevel.Warning, $"Sequence gap detected: expected {from}, received {received}. Sending ResendRequest {from}..0");
        using var rr = new FixMessageBuilder(MsgTypes.ResendRequest);
        rr.Set(Tags.BeginSeqNo, from).Set(Tags.EndSeqNo, 0);
        await SendAdminAsync(rr).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- outbound

    private async ValueTask SendLogonAsync(bool? echoReset)
    {
        var reset = echoReset ?? (_settings.ResetOnLogon || ResetNextLogon);
        ResetNextLogon = false;
        if (_settings.Role == SessionRole.Initiator && reset)
        {
            _store.Reset(Now);
        }

        _sentResetOnLogon = reset;
        using var logon = new FixMessageBuilder(MsgTypes.Logon);
        logon.Set(Tags.EncryptMethod, 0).Set(Tags.HeartBtInt, _heartBtInt);
        if (reset)
        {
            logon.Set(Tags.ResetSeqNumFlag, true);
        }

        logon.Set(Tags.DefaultApplVerID, _settings.DefaultApplVerID);

        await SendAdminAsync(logon).ConfigureAwait(false);
    }

    private async ValueTask SendLogoutAsync(string? text)
    {
        if (_transport is null)
        {
            return;
        }

        using var logout = new FixMessageBuilder(MsgTypes.Logout);
        logout.Set(Tags.Text, text);
        await SendAdminAsync(logout).ConfigureAwait(false);
    }

    private async ValueTask SendRejectAsync(FixMessage refMsg, SessionRejectReason reason, int refTag, string text)
    {
        using var reject = new FixMessageBuilder(MsgTypes.Reject);
        reject.Set(Tags.RefSeqNum, refMsg.MsgSeqNum)
            .Set(Tags.RefTagID, refTag)
            .Set(Tags.RefMsgType, refMsg.MsgType)
            .Set(Tags.SessionRejectReason, (int)reason)
            .Set(Tags.Text, text);
        await SendAdminAsync(reject).ConfigureAwait(false);
    }

    private async ValueTask SendGapFillAsync(int seq, int newSeqNo)
    {
        using var gf = new FixMessageBuilder(MsgTypes.SequenceReset);
        gf.Set(Tags.GapFillFlag, true).Set(Tags.NewSeqNo, newSeqNo);
        var header = new FixHeader(_settings.Id.BeginString, _settings.Id.SenderCompID, _settings.Id.TargetCompID, seq, Now,
            PossDupFlag: true, OrigSendingTime: Now);
        await TransmitAsync(gf.ToBytes(header), MessageDisposition.Resent, $"GapFill {seq}..{newSeqNo - 1}").ConfigureAwait(false);
    }

    /// <summary>Admin messages are stamped, stored (so a resend knows to gap-fill them) and sent if connected.</summary>
    private async ValueTask SendAdminAsync(FixMessageBuilder message)
    {
        if (_transport is null)
        {
            return;
        }

        var bytes = Stamp(message);
        await TransmitAsync(bytes, MessageDisposition.Normal, null).ConfigureAwait(false);
    }

    private async ValueTask SendApplicationAsync(FixMessageBuilder message)
    {
        using (message)
        {
            var bytes = Stamp(message);
            if (State == SessionState.Active && _transport is not null)
            {
                await TransmitAsync(bytes, MessageDisposition.Normal, null).ConfigureAwait(false);
            }
            else
            {
                Wire(FixDirection.Outbound, bytes, MessageDisposition.StoredWhileOffline,
                    "Session not logged on; will be delivered by resend after the next logon");
            }
        }
    }

    private byte[] Stamp(FixMessageBuilder message)
    {
        var seq = _store.NextSenderSeqNum;
        var header = new FixHeader(_settings.Id.BeginString, _settings.Id.SenderCompID, _settings.Id.TargetCompID, seq, Now);
        var bytes = message.ToBytes(header);
        _store.StoreOutbound(seq, bytes);
        _store.NextSenderSeqNum = seq + 1;
        return bytes;
    }

    private async ValueTask TransmitAsync(byte[] bytes, MessageDisposition disposition, string? note)
    {
        var transport = _transport;
        if (transport is null)
        {
            return;
        }

        var result = await transport.SendAsync(bytes, _stop.Token).ConfigureAwait(false);
        _lastSent = Now;
        if (result.FaultNote is not null && result.FaultNote.Contains("chaos", StringComparison.Ordinal))
        {
            Wire(FixDirection.Outbound, result.ActualBytes,
                result.Delivered ? MessageDisposition.CorruptedByFault : MessageDisposition.DroppedByFault, result.FaultNote);
            Log(SessionLogLevel.Warning, $"Chaos: {result.FaultNote}");
            return;
        }

        Wire(FixDirection.Outbound, result.ActualBytes, disposition, note);
        if (!result.Delivered)
        {
            _commands.Writer.TryWrite(new DisconnectCommand($"Send failed: {result.FaultNote}", _generation));
        }
    }

    // ---------------------------------------------------------------- observability

    private void SetState(SessionState next, string? reason)
    {
        var previous = State;
        State = next;
        _stateEnteredAt = Now;
        if (previous != next)
        {
            _observer?.OnState(new FixSessionStateEvent(Id, Now, previous, next, reason));
            SessionLog.StateChanged(_logger, Id.ToString(), previous, next, reason);
        }
    }

    private void Wire(FixDirection direction, byte[] raw, MessageDisposition disposition, string? note = null) =>
        _observer?.OnWire(new FixWireEvent(Id, direction, Now, raw, disposition, note));

    private void Log(SessionLogLevel level, string text)
    {
        _observer?.OnLog(new FixSessionLogEvent(Id, Now, level, text));
        SessionLog.Event(_logger, Id.ToString(), level, text);
    }

    private abstract record Command;

    private sealed record InboundCommand(byte[] Bytes, int Generation) : Command;

    private sealed record SendCommand(FixMessageBuilder Message) : Command;

    private sealed record TickCommand : Command;

    private sealed record AttachCommand(IFixTransport Transport, byte[]? FirstInbound) : Command;

    private sealed record LogoutCommand(string? Text) : Command;

    private sealed record DisconnectCommand(string Reason, int Generation) : Command;

    private sealed record ResetCommand : Command;

    private sealed record InvokeCommand(Func<ValueTask> Action) : Command;
}

internal static partial class SessionLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "{Session} {From} -> {To}: {Reason}")]
    public static partial void StateChanged(ILogger logger, string session, SessionState from, SessionState to, string? reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Session} [{Level}] {Text}")]
    public static partial void Event(ILogger logger, string session, SessionLogLevel level, string text);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Session} session loop error")]
    public static partial void LoopError(ILogger logger, Exception ex, string session);
}
