using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Tickwire.Engine;
using Tickwire.Fix;
using Tickwire.Fix.Session;
using Tickwire.Fix.Session.Store;

namespace Tickwire.Persistence;

/// <summary>
/// Session sequence numbers and the outbound resend store in MySQL, written with Dapper. This sits under
/// <see cref="WriteBehindSessionStore"/>, which batches writes off the session loop.
/// </summary>
public sealed class MySqlSessionStoreBackend(string connectionString) : ISessionStoreBackend
{
    public static string Key(SessionId id) => id.ToString();

    public async Task<StoreSnapshot?> LoadAsync(SessionId session, int maxMessages, CancellationToken cancellationToken)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        var key = Key(session);
        var state = await conn.QueryFirstOrDefaultAsync<(int NextSender, int NextTarget, DateTime Created)?>(new CommandDefinition(
            "SELECT NextSenderSeqNum, NextTargetSeqNum, CreationTime FROM session_state WHERE SessionKey = @key",
            new { key }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        var rows = await conn.QueryAsync<(int SeqNum, byte[] Raw)>(new CommandDefinition(
            """
            SELECT SeqNum, Raw FROM (
              SELECT SeqNum, Raw FROM session_messages
              WHERE SessionKey = @key AND IsStore = 1
              ORDER BY SeqNum DESC LIMIT @max) t
            ORDER BY SeqNum
            """, new { key, max = maxMessages }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var s = state.Value;
        return new StoreSnapshot(s.NextSender, s.NextTarget, DateTime.SpecifyKind(s.Created, DateTimeKind.Utc),
            [.. rows.Select(r => (r.SeqNum, r.Raw))]);
    }

    public async Task ApplyAsync(SessionId session, IReadOnlyList<StoreOp> ops, CancellationToken cancellationToken)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var key = Key(session);
        var messages = new List<object>();
        foreach (var op in ops)
        {
            switch (op)
            {
                case ResetOp r:
                    await FlushMessages(conn, tx, messages, cancellationToken).ConfigureAwait(false);
                    await conn.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM session_messages WHERE SessionKey = @key AND IsStore = 1", new { key }, tx,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                    await UpsertState(conn, tx, key, 1, 1, r.CreationTime, cancellationToken).ConfigureAwait(false);
                    break;
                case SetSeqNumsOp s:
                    await UpsertState(conn, tx, key, s.NextSenderSeqNum, s.NextTargetSeqNum, null, cancellationToken).ConfigureAwait(false);
                    break;
                case AddMessageOp a:
                    messages.Add(new
                    {
                        key,
                        seq = a.SeqNum,
                        type = FixParser.TryParse(a.Message, out var m, out _) ? m.MsgType : "?",
                        raw = a.Message,
                        ts = a.StoredAt,
                    });
                    break;
                default:
                    break;
            }
        }

        await FlushMessages(conn, tx, messages, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task FlushMessages(MySqlConnection conn, MySqlTransaction tx, List<object> messages, CancellationToken ct)
    {
        if (messages.Count == 0)
        {
            return Task.CompletedTask;
        }

        var batch = messages.ToArray();
        messages.Clear();
        return conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO session_messages (SessionKey, Direction, SeqNum, MsgType, Raw, Disposition, IsStore, Timestamp)
            VALUES (@key, 'O', @seq, @type, @raw, 'Store', 1, @ts)
            """, batch, tx, cancellationToken: ct));
    }

    private static Task UpsertState(MySqlConnection conn, MySqlTransaction tx, string key, int sender, int target, DateTime? created,
        CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO session_state (SessionKey, NextSenderSeqNum, NextTargetSeqNum, CreationTime, UpdatedAt)
            VALUES (@key, @sender, @target, COALESCE(@created, UTC_TIMESTAMP(6)), UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE NextSenderSeqNum = @sender, NextTargetSeqNum = @target,
              CreationTime = COALESCE(@created, CreationTime), UpdatedAt = UTC_TIMESTAMP(6)
            """, new { key, sender, target, created }, tx, cancellationToken: ct));
}

/// <summary>
/// Background writer for orders, executions and the wire archive. Producers enqueue and return immediately; a single
/// loop writes batches every 200 ms, so MySQL latency never reaches the OMS or session loops.
/// </summary>
public sealed partial class MySqlJournal : IOmsListener, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly Channel<object> _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(100_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    private readonly Task _loop;
    private readonly CancellationTokenSource _stop = new();

    public MySqlJournal(string connectionString, ILogger<MySqlJournal> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
        _loop = Task.Run(RunAsync);
    }

    public void OnExecutionReport(ExecutionReportEvent report)
    {
        if (report.Order.Id != 0)
        {
            _queue.Writer.TryWrite(report);
        }
    }

    public void OnCancelReject(CancelRejectEvent reject)
    {
    }

    public void Archive(FixWireEvent e) => _queue.Writer.TryWrite(e);

    private async Task RunAsync()
    {
        var orders = new Dictionary<long, OrderView>();
        var execs = new List<ExecutionReportEvent>();
        var wire = new List<FixWireEvent>();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (!await _queue.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
                {
                    break;
                }

                await Task.Delay(200, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_queue.Reader.TryRead(out var item) && execs.Count + wire.Count < 5000)
            {
                switch (item)
                {
                    case ExecutionReportEvent er:
                        orders[er.Order.Id] = er.Order;
                        execs.Add(er);
                        break;
                    case FixWireEvent w:
                        wire.Add(w);
                        break;
                    default:
                        break;
                }
            }

            try
            {
                await WriteAsync(orders.Values, execs, wire).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException)
            {
                JournalWriteFailed(_logger, ex, execs.Count, wire.Count);
            }

            orders.Clear();
            execs.Clear();
            wire.Clear();
        }
    }

    private async Task WriteAsync(IEnumerable<OrderView> orders, List<ExecutionReportEvent> execs, List<FixWireEvent> wire)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

        var orderRows = orders.Select(o => new
        {
            o.Id, o.OrderId, o.ClientId, o.ClOrdID, o.OrigClOrdID, Symbol = o.Contract.OccSymbol,
            Side = o.Side == Venue.Side.Buy ? "1" : "2", OrdType = o.OrdType == Venue.OrderType.Market ? "1" : "2",
            Tif = ((int)o.TimeInForce).ToString(System.Globalization.CultureInfo.InvariantCulture),
            o.Price, o.OrderQty, o.CumQty, o.LeavesQty, o.AvgPx, Status = ((char)o.Status).ToString(), Text = Trim(o.Text, 512),
            o.CreatedAt, o.UpdatedAt,
        }).ToArray();
        if (orderRows.Length > 0)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO orders (Id, OrderId, ClientId, ClOrdID, OrigClOrdID, Symbol, Side, OrdType, TimeInForce, Price, OrderQty,
                  CumQty, LeavesQty, AvgPx, Status, Text, CreatedAt, UpdatedAt)
                VALUES (@Id, @OrderId, @ClientId, @ClOrdID, @OrigClOrdID, @Symbol, @Side, @OrdType, @Tif, @Price, @OrderQty,
                  @CumQty, @LeavesQty, @AvgPx, @Status, @Text, @CreatedAt, @UpdatedAt)
                ON DUPLICATE KEY UPDATE ClOrdID = VALUES(ClOrdID), OrigClOrdID = VALUES(OrigClOrdID), Price = VALUES(Price),
                  OrderQty = VALUES(OrderQty), CumQty = VALUES(CumQty), LeavesQty = VALUES(LeavesQty), AvgPx = VALUES(AvgPx),
                  Status = VALUES(Status), Text = VALUES(Text), UpdatedAt = VALUES(UpdatedAt)
                """, orderRows, tx).ConfigureAwait(false);
        }

        var execRows = execs.Select(e => new
        {
            e.ExecId, OrderKey = e.Order.Id, e.Order.ClientId, e.Order.ClOrdID, ExecType = ((char)e.ExecType).ToString(),
            OrdStatus = ((char)e.Order.Status).ToString(), e.LastQty, e.LastPx, e.Order.CumQty, e.Order.LeavesQty,
            Text = Trim(e.Text, 512), e.TransactTime,
        }).ToArray();
        if (execRows.Length > 0)
        {
            await conn.ExecuteAsync(
                """
                INSERT IGNORE INTO executions (ExecId, OrderKey, ClientId, ClOrdID, ExecType, OrdStatus, LastQty, LastPx, CumQty,
                  LeavesQty, Text, TransactTime)
                VALUES (@ExecId, @OrderKey, @ClientId, @ClOrdID, @ExecType, @OrdStatus, @LastQty, @LastPx, @CumQty, @LeavesQty,
                  @Text, @TransactTime)
                """, execRows, tx).ConfigureAwait(false);
        }

        var wireRows = wire.Where(w => w.Raw.Length <= 8192).Select(w => new
        {
            Key = w.Session.ToString(), Direction = w.Direction == FixDirection.Inbound ? "I" : "O",
            Seq = FixParser.TryParse(w.Raw, out var m, out _) ? m.MsgSeqNum : 0, Type = m?.MsgType ?? "?", w.Raw,
            Disposition = w.Disposition.ToString(), w.Timestamp,
        }).ToArray();
        if (wireRows.Length > 0)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO session_messages (SessionKey, Direction, SeqNum, MsgType, Raw, Disposition, IsStore, Timestamp)
                VALUES (@Key, @Direction, @Seq, @Type, @Raw, @Disposition, 0, @Timestamp)
                """, wireRows, tx).ConfigureAwait(false);
        }

        await tx.CommitAsync().ConfigureAwait(false);
    }

    private static string? Trim(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private int _disposeState1;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState1, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Journal write failed ({Execs} executions, {Wire} wire messages dropped)")]
    private static partial void JournalWriteFailed(ILogger logger, Exception ex, int execs, int wire);
}
