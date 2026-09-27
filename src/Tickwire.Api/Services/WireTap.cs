using System.Collections.Concurrent;
using System.Threading.Channels;
using Tickwire.Engine;
using Tickwire.Fix;
using Tickwire.Fix.Dictionary;
using Tickwire.Fix.Session;
using Tickwire.Persistence;

namespace Tickwire.Api.Services;

public sealed record WireEventDto(
    long Id,
    string SessionKey,
    string Side,
    string Direction,
    DateTime Time,
    string Raw,
    string MsgType,
    string MsgTypeName,
    int SeqNum,
    string Disposition,
    string? Note,
    string? ClOrdID,
    bool PossDup);

public sealed record SessionLogDto(long Id, string SessionKey, string Side, DateTime Time, string Level, string Text);

public sealed record SessionStateDto(string SessionKey, string Side, DateTime Time, string From, string To, string? Reason);

/// <summary>A message for connected browsers: which SignalR group, which client method, what payload.</summary>
public sealed record LiveMessage(string Group, string Method, object Payload);

/// <summary>Fan-out point for live updates. Producers never block; the publisher drains it onto SignalR.</summary>
public sealed class LiveBus
{
    private readonly Channel<LiveMessage> _channel = Channel.CreateBounded<LiveMessage>(new BoundedChannelOptions(50_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    public ChannelReader<LiveMessage> Reader => _channel.Reader;

    public void Publish(string group, string method, object payload) => _channel.Writer.TryWrite(new LiveMessage(group, method, payload));
}

/// <summary>
/// Observes every session: keeps the last messages per session for the FIX Inspector, streams them live, feeds the
/// message-rate metrics and (when MySQL is configured) archives them.
/// Guest sessions have two sides (the venue's acceptor and the browser trader's initiator); both are grouped under the
/// venue-side session key so the Inspector can show either view.
/// </summary>
public sealed class WireTap
{
    private const int MaxEvents = 1000;
    private readonly ConcurrentDictionary<string, Tap> _taps = new(StringComparer.Ordinal);
    private readonly LiveBus _bus;
    private readonly EngineMetrics _metrics;
    private readonly MySqlJournal? _journal;
    private long _nextId;

    public WireTap(LiveBus bus, EngineMetrics metrics, MySqlJournal? journal = null)
    {
        _bus = bus;
        _metrics = metrics;
        _journal = journal;
    }

    public static string Group(string sessionKey) => $"fix:{sessionKey}";

    public IFixSessionObserver Observer(string sessionKey, string side, bool isVenueSide) =>
        new SideObserver(this, sessionKey, side, isVenueSide);

    public IReadOnlyList<WireEventDto> Recent(string sessionKey, int max = 300) =>
        _taps.TryGetValue(sessionKey, out var tap) ? tap.Snapshot(max) : [];

    public IReadOnlyList<SessionLogDto> RecentLogs(string sessionKey, int max = 200) =>
        _taps.TryGetValue(sessionKey, out var tap) ? tap.Logs(max) : [];

    public void Forget(string sessionKey) => _taps.TryRemove(sessionKey, out _);

    private Tap TapFor(string key) => _taps.GetOrAdd(key, _ => new Tap());

    private void OnWire(string key, string side, bool isVenueSide, FixWireEvent e)
    {
        string msgType = "?", clOrdId = null!;
        var seq = 0;
        var possDup = false;
        if (FixParser.TryParse(e.Raw, out var m, out _))
        {
            msgType = m.MsgType;
            seq = m.MsgSeqNum;
            possDup = m.PossDupFlag;
            clOrdId = m.GetString(Tags.ClOrdID)!;
        }

        var dto = new WireEventDto(Interlocked.Increment(ref _nextId), key, side,
            e.Direction == FixDirection.Inbound ? "in" : "out", e.Timestamp, FixDisplay.ToPiped(e.Raw), msgType,
            FixDictionary.Fix44.MessageName(msgType), seq, e.Disposition.ToString(), e.Note, clOrdId, possDup);
        TapFor(key).Add(dto);
        _bus.Publish(Group(key), "wire", dto);

        if (isVenueSide)
        {
            if (e.Direction == FixDirection.Inbound)
            {
                _metrics.MessagesIn.Increment();
            }
            else if (e.Disposition != MessageDisposition.DroppedByFault)
            {
                _metrics.MessagesOut.Increment();
            }

            _journal?.Archive(e);
        }
    }

    private void OnLog(string key, string side, FixSessionLogEvent e)
    {
        var dto = new SessionLogDto(Interlocked.Increment(ref _nextId), key, side, e.Timestamp, e.Level.ToString(), e.Text);
        TapFor(key).AddLog(dto);
        _bus.Publish(Group(key), "sessionLog", dto);
    }

    private void OnState(string key, string side, FixSessionStateEvent e)
    {
        var dto = new SessionStateDto(key, side, e.Timestamp, e.From.ToString(), e.To.ToString(), e.Reason);
        _bus.Publish(Group(key), "sessionState", dto);
        _bus.Publish("ops", "sessionState", dto);
        OnLog(key, side, new FixSessionLogEvent(e.Session, e.Timestamp, SessionLogLevel.Info, $"{e.From} → {e.To}{(e.Reason is null ? "" : $": {e.Reason}")}"));
    }

    private sealed class SideObserver(WireTap tap, string key, string side, bool isVenueSide) : IFixSessionObserver
    {
        public void OnWire(FixWireEvent e) => tap.OnWire(key, side, isVenueSide, e);

        public void OnLog(FixSessionLogEvent e) => tap.OnLog(key, side, e);

        public void OnState(FixSessionStateEvent e) => tap.OnState(key, side, e);
    }

    private sealed class Tap
    {
        private readonly Lock _lock = new();
        private readonly Queue<WireEventDto> _events = new();
        private readonly Queue<SessionLogDto> _logs = new();

        public void Add(WireEventDto e)
        {
            lock (_lock)
            {
                _events.Enqueue(e);
                if (_events.Count > MaxEvents)
                {
                    _events.Dequeue();
                }
            }
        }

        public void AddLog(SessionLogDto e)
        {
            lock (_lock)
            {
                _logs.Enqueue(e);
                if (_logs.Count > 300)
                {
                    _logs.Dequeue();
                }
            }
        }

        public IReadOnlyList<WireEventDto> Snapshot(int max)
        {
            lock (_lock)
            {
                return [.. _events.Skip(Math.Max(0, _events.Count - max))];
            }
        }

        public IReadOnlyList<SessionLogDto> Logs(int max)
        {
            lock (_lock)
            {
                return [.. _logs.Skip(Math.Max(0, _logs.Count - max))];
            }
        }
    }
}
