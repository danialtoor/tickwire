using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Tickwire.Fix.Session.Store;

public abstract record StoreOp;

public sealed record SetSeqNumsOp(int NextSenderSeqNum, int NextTargetSeqNum) : StoreOp;

public sealed record AddMessageOp(int SeqNum, byte[] Message, DateTime StoredAt) : StoreOp;

public sealed record ResetOp(DateTime CreationTime) : StoreOp;

public sealed record StoreSnapshot(int NextSenderSeqNum, int NextTargetSeqNum, DateTime CreationTime,
    IReadOnlyList<(int SeqNum, byte[] Message)> RecentMessages);

/// <summary>Durable storage for session state (MySQL in production).</summary>
public interface ISessionStoreBackend
{
    Task<StoreSnapshot?> LoadAsync(SessionId session, int maxMessages, CancellationToken cancellationToken);

    Task ApplyAsync(SessionId session, IReadOnlyList<StoreOp> ops, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps the session's state in memory and persists changes in the background, in batches.
/// The session loop never waits on the database, so a slow write can't stall heartbeats or order flow.
/// The trade-off: a crash can lose the last few milliseconds of sequence-number updates. On restart the counterparty's
/// sequence check (too high → ResendRequest, too low → PossDup ignored) repairs that. See ADR 0004.
/// </summary>
public sealed class WriteBehindSessionStore : ISessionStore, IAsyncDisposable
{
    private readonly MemorySessionStore _memory;
    private readonly ISessionStoreBackend _backend;
    private readonly SessionId _session;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<StoreOp> _ops = Channel.CreateUnbounded<StoreOp>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _flushLoop;
    private readonly CancellationTokenSource _stop = new();

    private WriteBehindSessionStore(SessionId session, MemorySessionStore memory, ISessionStoreBackend backend,
        TimeProvider time, ILogger logger)
    {
        _session = session;
        _memory = memory;
        _backend = backend;
        _time = time;
        _logger = logger;
        _flushLoop = Task.Run(FlushLoopAsync);
    }

    public static async Task<WriteBehindSessionStore> OpenAsync(SessionId session, ISessionStoreBackend backend,
        TimeProvider time, ILogger logger, CancellationToken cancellationToken)
    {
        var snapshot = await backend.LoadAsync(session, 20_000, cancellationToken).ConfigureAwait(false);
        var memory = new MemorySessionStore(snapshot?.CreationTime ?? time.GetUtcNow().UtcDateTime);
        if (snapshot is not null)
        {
            memory.NextSenderSeqNum = snapshot.NextSenderSeqNum;
            memory.NextTargetSeqNum = snapshot.NextTargetSeqNum;
            foreach (var (seq, msg) in snapshot.RecentMessages)
            {
                memory.StoreOutbound(seq, msg);
            }
        }

        var store = new WriteBehindSessionStore(session, memory, backend, time, logger);
        if (snapshot is null)
        {
            store._ops.Writer.TryWrite(new ResetOp(memory.CreationTime));
        }

        return store;
    }

    public int NextSenderSeqNum
    {
        get => _memory.NextSenderSeqNum;
        set
        {
            _memory.NextSenderSeqNum = value;
            QueueSeqNums();
        }
    }

    public int NextTargetSeqNum
    {
        get => _memory.NextTargetSeqNum;
        set
        {
            _memory.NextTargetSeqNum = value;
            QueueSeqNums();
        }
    }

    public DateTime CreationTime => _memory.CreationTime;

    public void StoreOutbound(int seqNum, byte[] message)
    {
        _memory.StoreOutbound(seqNum, message);
        _ops.Writer.TryWrite(new AddMessageOp(seqNum, message, _time.GetUtcNow().UtcDateTime));
    }

    public IReadOnlyList<(int SeqNum, byte[] Message)> GetOutbound(int begin, int end) => _memory.GetOutbound(begin, end);

    public void Reset(DateTime creationTime)
    {
        _memory.Reset(creationTime);
        _ops.Writer.TryWrite(new ResetOp(creationTime));
    }

    /// <summary>Waits until everything queued so far has been written.</summary>
    public async Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ops.Writer.TryWrite(new FlushMarker(done));
        await done.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _ops.Writer.TryComplete();
        try
        {
            await _flushLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private void QueueSeqNums() =>
        _ops.Writer.TryWrite(new SetSeqNumsOp(_memory.NextSenderSeqNum, _memory.NextTargetSeqNum));

    private async Task FlushLoopAsync()
    {
        var batch = new List<StoreOp>(256);
        var markers = new List<FlushMarker>();
        var reader = _ops.Reader;
        while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
        {
            SetSeqNumsOp? lastSeq = null;
            while (batch.Count < 1000 && reader.TryRead(out var op))
            {
                switch (op)
                {
                    case FlushMarker m:
                        markers.Add(m);
                        break;
                    case SetSeqNumsOp s:
                        lastSeq = s; // only the latest value matters
                        break;
                    default:
                        if (lastSeq is not null)
                        {
                            batch.Add(lastSeq);
                            lastSeq = null;
                        }

                        batch.Add(op);
                        break;
                }
            }

            if (lastSeq is not null)
            {
                batch.Add(lastSeq);
            }

            if (batch.Count > 0)
            {
                await WriteWithRetryAsync(batch).ConfigureAwait(false);
                batch.Clear();
            }

            foreach (var m in markers)
            {
                m.Done.TrySetResult();
            }

            markers.Clear();
        }
    }

    private async Task WriteWithRetryAsync(List<StoreOp> batch)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _backend.ApplyAsync(_session, batch, _stop.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StoreLog.WriteFailed(_logger, ex, _session.ToString(), attempt);
                if (attempt >= 5)
                {
                    return; // Give up on this batch; the in-memory state is still authoritative.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), _time, _stop.Token).ConfigureAwait(false);
            }
        }
    }

    private sealed record FlushMarker(TaskCompletionSource Done) : StoreOp;
}

internal static partial class StoreLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Session store write for {Session} failed (attempt {Attempt})")]
    public static partial void WriteFailed(ILogger logger, Exception ex, string session, int attempt);
}
