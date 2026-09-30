using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tickwire.Engine;
using Tickwire.Fix.Session.Transport;
using Tickwire.Persistence;
using Tickwire.Venue;

namespace Tickwire.Api.Services;

/// <summary>
/// Starts the trading core in order: database migrations, venue, OMS, then the FIX acceptor. Stops in reverse.
/// </summary>
public sealed partial class EngineHost(
    SimulatedVenue venue,
    OrderManager oms,
    FixOrderGateway gateway,
    EngineMetrics metrics,
    LiveOrderPublisher livePublisher,
    PositionKeeper positions,
    SessionManager sessions,
    IOptions<TickwireOptions> options,
    IServiceProvider services,
    ILogger<EngineHost> logger) : IHostedService
{
    private FixAcceptor? _acceptor;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IDbContextFactory<TickwireDbContext>>() is { } dbFactory)
        {
            await MigrateWithRetryAsync(dbFactory, cancellationToken).ConfigureAwait(false);
        }

        oms.AddListener(gateway);
        oms.AddListener(metrics);
        oms.AddListener(livePublisher);
        oms.AddListener(positions);
        oms.PortfolioRisk = positions;
        if (services.GetService<MySqlJournal>() is { } journal)
        {
            oms.AddListener(journal);
        }

        venue.SetSink(oms);
        venue.Start();
        oms.Start();

        _acceptor = new FixAcceptor(sessions, services.GetRequiredService<ILoggerFactory>().CreateLogger<FixAcceptor>());
        sessions.Acceptor = _acceptor;
        await _acceptor.StartTcpAsync(new IPEndPoint(IPAddress.IPv6Any, options.Value.FixPort)).ConfigureAwait(false);
        Started(logger, options.Value.FixPort);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await sessions.DisposeAsync().ConfigureAwait(false);
        if (_acceptor is not null)
        {
            await _acceptor.DisposeAsync().ConfigureAwait(false);
        }

        await oms.DisposeAsync().ConfigureAwait(false);
        await venue.DisposeAsync().ConfigureAwait(false);
    }

    private async Task MigrateWithRetryAsync(IDbContextFactory<TickwireDbContext> factory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                await db.Database.MigrateAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < 30 && ex is not OperationCanceledException)
            {
                MigrationRetry(logger, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tickwire engine started; FIX acceptor on port {Port}")]
    private static partial void Started(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database not ready (attempt {Attempt}): {Error}")]
    private static partial void MigrationRetry(ILogger logger, int attempt, string error);
}

/// <summary>
/// Hourly: removes expired guests, their sessions, orders and stored messages, then applies retention (wire archive
/// older than 3 days, audit log older than 30).
/// </summary>
public sealed partial class Housekeeping(IClientRepository repo, SessionManager sessions, FeedManager feeds, TimeProvider time,
    ILogger<Housekeeping> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                await PurgeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PurgeFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task<int> PurgeAsync(CancellationToken ct, DateTime? asOf = null)
    {
        var removed = await repo.PurgeExpiredGuestsAsync(asOf ?? time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        foreach (var client in removed)
        {
            await feeds.DisconnectAsync(client.ClientId).ConfigureAwait(false);
            await sessions.RemoveClientAsync(client).ConfigureAwait(false);
        }

        if (removed.Count > 0)
        {
            Purged(logger, removed.Count);
        }

        var now = asOf ?? time.GetUtcNow().UtcDateTime;
        var pruned = await repo.PruneAsync(now.AddDays(-3), now.AddDays(-30), ct).ConfigureAwait(false);
        if (pruned > 0)
        {
            Pruned(logger, pruned);
        }

        return removed.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} expired guest(s)")]
    private static partial void Purged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention removed {Count} old row(s)")]
    private static partial void Pruned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Guest purge failed")]
    private static partial void PurgeFailed(ILogger logger, Exception ex);
}
