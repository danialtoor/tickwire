using Tickwire.Fix.Dictionary;

namespace Tickwire.Fix.Session;

/// <summary>Identifies a session from our side: we are <see cref="SenderCompID"/>, the counterparty is <see cref="TargetCompID"/>.</summary>
public readonly record struct SessionId(string BeginString, string SenderCompID, string TargetCompID)
{
    public override string ToString() => $"{BeginString}:{SenderCompID}->{TargetCompID}";
}

public enum SessionRole
{
    /// <summary>Waits for the counterparty to connect and send Logon (a venue or gateway).</summary>
    Acceptor,

    /// <summary>Connects and sends Logon (a client).</summary>
    Initiator,
}

public enum SessionState
{
    Disconnected,
    AwaitingLogon,
    Active,
    AwaitingLogoutAck,
}

public sealed record SessionSettings
{
    public required SessionId Id { get; init; }
    public SessionRole Role { get; init; } = SessionRole.Acceptor;

    /// <summary>Heartbeat interval in seconds. An acceptor adopts the value the initiator sends on Logon.</summary>
    public int HeartBtInt { get; init; } = 30;

    public int MinHeartBtInt { get; init; } = 1;
    public int MaxHeartBtInt { get; init; } = 300;

    /// <summary>Initiator: send ResetSeqNumFlag(141)=Y on every Logon, starting both sides from 1.</summary>
    public bool ResetOnLogon { get; init; }

    /// <summary>Reset sequence numbers after a clean logout (common for day-scoped sessions).</summary>
    public bool ResetOnLogout { get; init; }

    public TimeSpan LogonTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan LogoutTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Largest allowed difference between SendingTime(52) and our clock. Zero disables the check.</summary>
    public TimeSpan MaxLatency { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Validate every inbound message against the data dictionary and answer problems with Reject(3).</summary>
    public bool ValidateMessages { get; init; } = true;

    public FixDictionary Dictionary { get; init; } = FixDictionary.Fix44;

    /// <summary>
    /// FIXT 1.1 sessions only: the application version both sides use, sent as DefaultApplVerID(1137) on Logon and
    /// required from the peer. "9" is FIX 5.0 SP2. Null for FIX 4.x sessions.
    /// </summary>
    public string? DefaultApplVerID { get; init; }
}

public enum FixDirection
{
    Inbound,
    Outbound,
}

/// <summary>What happened to a message on the wire. Drives the highlighting in the FIX Inspector.</summary>
public enum MessageDisposition
{
    Normal,

    /// <summary>A stored application message sent again with PossDupFlag=Y in answer to a ResendRequest.</summary>
    Resent,

    /// <summary>Inbound with a sequence number above the expected one; held until the gap is filled.</summary>
    Queued,

    /// <summary>Inbound PossDup message whose sequence number was already processed.</summary>
    Duplicate,

    /// <summary>Inbound with bad CheckSum/BodyLength or unparseable. Ignored per the FIX spec.</summary>
    Garbled,

    /// <summary>Inbound that failed validation and was answered with Reject(3).</summary>
    Rejected,

    /// <summary>Outbound that a chaos fault swallowed. It is stored, so a ResendRequest can still recover it.</summary>
    DroppedByFault,

    /// <summary>Outbound that a chaos fault modified before sending.</summary>
    CorruptedByFault,

    /// <summary>Outbound application message generated while the session was offline. Delivered via resend after logon.</summary>
    StoredWhileOffline,
}

public sealed record FixWireEvent(
    SessionId Session,
    FixDirection Direction,
    DateTime Timestamp,
    byte[] Raw,
    MessageDisposition Disposition,
    string? Note = null);

public enum SessionLogLevel
{
    Info,
    Warning,
    Error,
}

public sealed record FixSessionLogEvent(SessionId Session, DateTime Timestamp, SessionLogLevel Level, string Text);

public sealed record FixSessionStateEvent(SessionId Session, DateTime Timestamp, SessionState From, SessionState To, string? Reason);

/// <summary>Receives everything a session does. Implementations must be fast and must not throw.</summary>
public interface IFixSessionObserver
{
    void OnWire(FixWireEvent e);

    void OnLog(FixSessionLogEvent e);

    void OnState(FixSessionStateEvent e);
}

/// <summary>The application layer on top of a session. Callbacks run on the session's loop, in sequence order.</summary>
public interface IFixApplication
{
    void OnLogon(FixSession session);

    void OnLogout(FixSession session);

    /// <summary>Called for every in-sequence, validated application message.</summary>
    ValueTask OnMessageAsync(FixSession session, FixMessage message, CancellationToken cancellationToken);
}

/// <summary>An application that ignores everything; handy for tests and initiators that only send.</summary>
public class NullFixApplication : IFixApplication
{
    public virtual void OnLogon(FixSession session)
    {
    }

    public virtual void OnLogout(FixSession session)
    {
    }

    public virtual ValueTask OnMessageAsync(FixSession session, FixMessage message, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
