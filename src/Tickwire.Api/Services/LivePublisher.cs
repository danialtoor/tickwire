using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Tickwire.Api.Hubs;
using Tickwire.Engine;
using Tickwire.Venue;

namespace Tickwire.Api.Services;

public sealed record BlotterRowDto(
    string OrderId,
    string ClOrdID,
    string? OrigClOrdID,
    int ContractId,
    string Display,
    string Occ,
    string Side,
    string Type,
    string Tif,
    decimal? Price,
    decimal Quantity,
    decimal CumQty,
    decimal LeavesQty,
    decimal AvgPx,
    string Status,
    string? Text,
    DateTime Created,
    DateTime Updated,
    string LastExecType,
    decimal LastQty,
    decimal LastPx)
{
    /// <summary>Legs of a spread order; null for single-contract orders.</summary>
    public IReadOnlyList<BlotterLegDto>? Legs { get; init; }

    /// <summary>Where the order went (SMART for spreads, whose legs route separately), what the client asked for, and fees so far.</summary>
    public string? Exchange { get; init; }

    public string? Destination { get; init; }
    public decimal Fees { get; init; }

    public static BlotterRowDto From(OrderView o, ExecType lastExecType = ExecType.OrderStatus, decimal lastQty = 0, decimal lastPx = 0,
        string? text = null) =>
        new(o.Id == 0 ? $"REJ-{o.ClOrdID}" : o.OrderId, o.ClOrdID, o.OrigClOrdID, o.Contract.Id,
            o.Contract.Id == 0 ? o.Contract.Underlying : o.Display, o.IsMultileg ? "" : o.Contract.OccSymbol,
            o.Side == Tickwire.Venue.Side.Buy ? "Buy" : "Sell", o.OrdType.ToString(), o.TimeInForce switch
            {
                TimeInForce.ImmediateOrCancel => "IOC",
                TimeInForce.FillOrKill => "FOK",
                _ => "Day",
            }, o.Price, o.OrderQty, o.CumQty, o.LeavesQty, o.AvgPx, o.Status.ToString(), text ?? o.Text, o.CreatedAt, o.UpdatedAt,
            lastExecType.ToString(), lastQty, lastPx)
        {
            Legs = o.Legs?.Select(l => new BlotterLegDto(l.Contract.Id, l.Contract.Display, l.Ratio, l.Side == Tickwire.Venue.Side.Buy ? "Buy" : "Sell"))
                .ToList(),
            Exchange = o.Exchange,
            Destination = o.Destination,
            Fees = o.Fees,
        };
}

public sealed record BlotterLegDto(int ContractId, string Display, int Ratio, string Side);

public sealed record SessionSummaryDto(
    string Key,
    string ClientId,
    string ClientCompId,
    string VenueCompId,
    string Transport,
    string State,
    int NextSenderSeqNum,
    int NextTargetSeqNum,
    int HeartBtInt,
    string? Remote,
    bool Chaos,
    bool IsGuest,
    DateTime? LastReceived);

public sealed record OpsDto(
    DateTime Time,
    double UptimeSeconds,
    double MsgInPerSec,
    double MsgOutPerSec,
    long MsgInTotal,
    long MsgOutTotal,
    IReadOnlyList<long> InSeries,
    IReadOnlyList<long> OutSeries,
    LatencySummary OrderToAckMicros,
    long OrdersAccepted,
    long Fills,
    long ContractsTraded,
    long CancelRejects,
    IReadOnlyDictionary<string, long> RejectsByReason,
    int SessionsActive,
    int SessionsTotal,
    IReadOnlyList<SessionSummaryDto> Sessions,
    bool GlobalKillSwitch,
    string Persistence);

/// <summary>Pushes OMS results to the owning client's "orders" group.</summary>
public sealed class LiveOrderPublisher(LiveBus bus) : IOmsListener
{
    public void OnExecutionReport(ExecutionReportEvent report) =>
        bus.Publish($"orders:{report.Order.ClientId}", "order",
            BlotterRowDto.From(report.Order, report.ExecType, report.LastQty, report.LastPx, report.Text));

    public void OnCancelReject(CancelRejectEvent reject) =>
        bus.Publish($"orders:{reject.ClientId}", "cancelReject", reject);
}

/// <summary>Remembers which order books someone is watching so only those are streamed.</summary>
public sealed class BookSubscriptions
{
    private readonly ConcurrentDictionary<int, long> _books = new();

    public void Touch(int contractId) => _books[contractId] = Stopwatch.GetTimestamp();

    public IEnumerable<int> Active => _books.Where(kv => Stopwatch.GetElapsedTime(kv.Value) < TimeSpan.FromMinutes(30)).Select(kv => kv.Key);
}

/// <summary>
/// Streams to SignalR: drains the live bus as events happen, publishes market snapshots at 4 Hz (only when data
/// changed) and the ops dashboard at 1 Hz.
/// </summary>
public sealed class LivePublisher(
    IHubContext<LiveHub> hub,
    LiveBus bus,
    MarketView market,
    MarketDataCache cache,
    InstrumentRegistry instruments,
    BookSubscriptions books,
    OpsSnapshotter ops,
    PositionKeeper positions,
    ILogger<LivePublisher> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(DrainBusAsync(stoppingToken), MarketLoopAsync(stoppingToken), OpsLoopAsync(stoppingToken));

    private async Task DrainBusAsync(CancellationToken ct)
    {
        await foreach (var msg in bus.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await hub.Clients.Group(msg.Group).SendAsync(msg.Method, msg.Payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Live send failed");
            }
        }
    }

    private async Task MarketLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        long lastVersion = -1;
        DateTime lastPrint = default;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var version = cache.Version;
            if (version == lastVersion)
            {
                continue;
            }

            lastVersion = version;
            try
            {
                foreach (var u in instruments.Underlyings)
                {
                    foreach (var expiry in instruments.Chain(u.Symbol).Select(c => c.Expiry).Distinct())
                    {
                        var chain = market.Chain(u.Symbol, expiry);
                        await hub.Clients.Group($"chain:{u.Symbol}:{expiry:yyyy-MM-dd}").SendAsync("chain", chain, ct).ConfigureAwait(false);
                    }
                }

                foreach (var id in books.Active)
                {
                    await hub.Clients.Group($"book:{id}").SendAsync("book", market.Book(id), ct).ConfigureAwait(false);
                }

                await hub.Clients.Group("market").SendAsync("underlyings", market.Underlyings(), ct).ConfigureAwait(false);
                var prints = market.Tape(null, 20).Where(p => p.Time > lastPrint).ToList();
                if (prints.Count > 0)
                {
                    lastPrint = prints.Max(p => p.Time);
                    await hub.Clients.Group("market").SendAsync("tape", prints, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Market publish failed");
            }
        }
    }

    private async Task OpsLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await hub.Clients.Group("ops").SendAsync("ops", ops.Snapshot(), ct).ConfigureAwait(false);
                foreach (var clientId in positions.Clients)
                {
                    await hub.Clients.Group($"orders:{clientId}").SendAsync("portfolio", positions.Snapshot(clientId), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Ops publish failed");
            }
        }
    }
}

public sealed class OpsSnapshotter(EngineMetrics metrics, SessionManager sessions, OrderManager oms, TimeProvider time, PersistenceMode mode)
{
    private readonly long _started = Stopwatch.GetTimestamp();

    public OpsDto Snapshot()
    {
        var list = sessions.Sessions.Select(s => new SessionSummaryDto(s.Key, s.Account.ClientId, s.Config.ClientCompId, s.Config.VenueCompId,
            s.Config.Transport, s.Session.State.ToString(), s.Session.NextSenderSeqNum, s.Session.NextTargetSeqNum, s.Session.HeartBtInt,
            s.Session.RemoteDescription, s.Config.EnableChaos, s.Account.IsGuest, s.Session.LastReceivedAt))
            .OrderByDescending(s => s.State == "Active").ThenBy(s => s.Key).ToList();
        return new OpsDto(time.GetUtcNow().UtcDateTime, Stopwatch.GetElapsedTime(_started).TotalSeconds,
            Math.Round(metrics.MessagesIn.PerSecond(), 1), Math.Round(metrics.MessagesOut.PerSecond(), 1),
            metrics.MessagesIn.Total, metrics.MessagesOut.Total, metrics.MessagesIn.Series(60), metrics.MessagesOut.Series(60),
            metrics.OrderToAck.Summary(), metrics.OrdersAccepted, metrics.Fills, metrics.ContractsTraded, metrics.CancelRejects,
            metrics.RejectsByReason.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            list.Count(s => s.State == "Active"), list.Count, list, oms.GlobalKillSwitch, mode.Name);
    }
}

public sealed record PersistenceMode(string Name);
