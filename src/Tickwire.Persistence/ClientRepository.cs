using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Tickwire.Engine;
using Tickwire.Venue;

namespace Tickwire.Persistence;

public sealed record SessionConfig(
    int Id,
    string ClientId,
    string BeginString,
    string ClientCompId,
    string VenueCompId,
    int HeartBtInt,
    bool ResetOnLogon,
    string Transport,
    bool EnableChaos)
{
    public string Key => $"{BeginString}:{VenueCompId}->{ClientCompId}";
}

public sealed record ClientRecord(
    string ClientId,
    string DisplayName,
    bool IsGuest,
    bool KillSwitch,
    RiskLimits Limits,
    IReadOnlyList<SessionConfig> Sessions,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    string? OwnerTokenHash);

public sealed record AuditRecord(DateTime Timestamp, string Actor, string Action, string? ClientId, string? Details);

/// <summary>Client configuration: accounts, their FIX sessions and risk limits, plus the admin audit trail.</summary>
public interface IClientRepository
{
    Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken ct = default);

    Task<ClientRecord?> GetAsync(string clientId, CancellationToken ct = default);

    Task<(ClientRecord Client, SessionConfig Session)?> FindBySessionAsync(string beginString, string clientCompId, string venueCompId,
        CancellationToken ct = default);

    Task CreateAsync(ClientRecord client, CancellationToken ct = default);

    Task UpdateLimitsAsync(string clientId, RiskLimits limits, CancellationToken ct = default);

    Task SetKillSwitchAsync(string clientId, bool engaged, CancellationToken ct = default);

    Task<SessionConfig> AddSessionAsync(SessionConfig session, CancellationToken ct = default);

    /// <summary>Deletes guests whose TTL has passed. Returns the removed clients so callers can tear down sessions.</summary>
    Task<IReadOnlyList<ClientRecord>> PurgeExpiredGuestsAsync(DateTime now, CancellationToken ct = default);

    Task AuditAsync(string actor, string action, string? clientId, string? details, CancellationToken ct = default);

    Task<IReadOnlyList<AuditRecord>> RecentAuditAsync(int max, CancellationToken ct = default);
}

public static class LimitsMapping
{
    public static RiskLimits ToDomain(RiskLimitsEntity? e) => e is null
        ? new RiskLimits()
        : new RiskLimits
        {
            MaxOrderQty = e.MaxOrderQty,
            MaxNotional = e.MaxNotional,
            PriceBandPct = e.PriceBandPct,
            PriceBandMinAbs = e.PriceBandMinAbs,
            MaxOpenOrders = e.MaxOpenOrders,
            AllowedUnderlyings = string.IsNullOrWhiteSpace(e.AllowedUnderlyings)
                ? null
                : e.AllowedUnderlyings.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AllowedOrderTypes = [.. e.AllowedOrderTypes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<OrderType>)],
            AllowedTimeInForce = [.. e.AllowedTimeInForce.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<TimeInForce>)],
            MaxMessagesPerSecond = e.MaxMessagesPerSecond,
            CancelOnDisconnect = e.CancelOnDisconnect,
        };

    public static RiskLimitsEntity ToEntity(string clientId, RiskLimits l, DateTime now) => new()
    {
        ClientId = clientId,
        MaxOrderQty = l.MaxOrderQty,
        MaxNotional = l.MaxNotional,
        PriceBandPct = l.PriceBandPct,
        PriceBandMinAbs = l.PriceBandMinAbs,
        MaxOpenOrders = l.MaxOpenOrders,
        AllowedUnderlyings = l.AllowedUnderlyings is { Count: > 0 } u ? string.Join(',', u) : null,
        AllowedOrderTypes = string.Join(',', l.AllowedOrderTypes),
        AllowedTimeInForce = string.Join(',', l.AllowedTimeInForce),
        MaxMessagesPerSecond = l.MaxMessagesPerSecond,
        CancelOnDisconnect = l.CancelOnDisconnect,
        UpdatedAt = now,
    };

    public static SessionConfig ToDomain(FixSessionEntity s) =>
        new(s.Id, s.ClientId, s.BeginString, s.ClientCompId, s.VenueCompId, s.HeartBtInt, s.ResetOnLogon, s.Transport, s.EnableChaos);

    public static ClientRecord ToDomain(ClientEntity c) => new(c.ClientId, c.DisplayName, c.IsGuest, c.KillSwitch, ToDomain(c.Limits),
        [.. c.Sessions.Select(ToDomain)], c.CreatedAt, c.ExpiresAt, c.OwnerTokenHash);
}

/// <summary>MySQL-backed repository (EF Core). Configuration traffic is low-volume, so EF's convenience wins here.</summary>
public sealed class EfClientRepository(IDbContextFactory<TickwireDbContext> factory, TimeProvider time) : IClientRepository
{
    public async Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Clients.AsNoTracking().Include(c => c.Limits).Include(c => c.Sessions).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        return [.. rows.Select(LimitsMapping.ToDomain)];
    }

    public async Task<ClientRecord?> GetAsync(string clientId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Clients.AsNoTracking().Include(c => c.Limits).Include(c => c.Sessions)
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        return row is null ? null : LimitsMapping.ToDomain(row);
    }

    public async Task<(ClientRecord Client, SessionConfig Session)?> FindBySessionAsync(string beginString, string clientCompId,
        string venueCompId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var session = await db.FixSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.BeginString == beginString && s.ClientCompId == clientCompId && s.VenueCompId == venueCompId, ct);
        if (session is null)
        {
            return null;
        }

        var client = await GetAsync(session.ClientId, ct);
        return client is null ? null : (client, LimitsMapping.ToDomain(session));
    }

    public async Task CreateAsync(ClientRecord client, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        db.Clients.Add(new ClientEntity
        {
            ClientId = client.ClientId,
            DisplayName = client.DisplayName,
            IsGuest = client.IsGuest,
            KillSwitch = client.KillSwitch,
            CreatedAt = client.CreatedAt,
            ExpiresAt = client.ExpiresAt,
            OwnerTokenHash = client.OwnerTokenHash,
            Limits = LimitsMapping.ToEntity(client.ClientId, client.Limits, now),
            Sessions =
            [
                .. client.Sessions.Select(s => new FixSessionEntity
                {
                    ClientId = client.ClientId,
                    BeginString = s.BeginString,
                    ClientCompId = s.ClientCompId,
                    VenueCompId = s.VenueCompId,
                    HeartBtInt = s.HeartBtInt,
                    ResetOnLogon = s.ResetOnLogon,
                    Transport = s.Transport,
                    EnableChaos = s.EnableChaos,
                    CreatedAt = now,
                }),
            ],
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateLimitsAsync(string clientId, RiskLimits limits, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = LimitsMapping.ToEntity(clientId, limits, time.GetUtcNow().UtcDateTime);
        var existing = await db.RiskLimits.FirstOrDefaultAsync(l => l.ClientId == clientId, ct);
        if (existing is null)
        {
            db.RiskLimits.Add(entity);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(entity);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task SetKillSwitchAsync(string clientId, bool engaged, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Clients.Where(c => c.ClientId == clientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.KillSwitch, engaged), ct);
    }

    public async Task<SessionConfig> AddSessionAsync(SessionConfig session, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = new FixSessionEntity
        {
            ClientId = session.ClientId,
            BeginString = session.BeginString,
            ClientCompId = session.ClientCompId,
            VenueCompId = session.VenueCompId,
            HeartBtInt = session.HeartBtInt,
            ResetOnLogon = session.ResetOnLogon,
            Transport = session.Transport,
            EnableChaos = session.EnableChaos,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.FixSessions.Add(entity);
        await db.SaveChangesAsync(ct);
        return LimitsMapping.ToDomain(entity);
    }

    public async Task<IReadOnlyList<ClientRecord>> PurgeExpiredGuestsAsync(DateTime now, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var expired = await db.Clients.Include(c => c.Limits).Include(c => c.Sessions)
            .Where(c => c.IsGuest && c.ExpiresAt != null && c.ExpiresAt < now).ToListAsync(ct);
        if (expired.Count == 0)
        {
            return [];
        }

        var ids = expired.Select(c => c.ClientId).ToList();
        var keys = expired.SelectMany(c => c.Sessions).Select(s => $"{s.BeginString}:{s.VenueCompId}->{s.ClientCompId}").ToList();
        await db.SessionMessages.Where(m => keys.Contains(m.SessionKey)).ExecuteDeleteAsync(ct);
        await db.SessionStates.Where(s => keys.Contains(s.SessionKey)).ExecuteDeleteAsync(ct);
        await db.Executions.Where(e => ids.Contains(e.ClientId)).ExecuteDeleteAsync(ct);
        await db.Orders.Where(o => ids.Contains(o.ClientId)).ExecuteDeleteAsync(ct);
        var records = expired.Select(LimitsMapping.ToDomain).ToList();
        db.Clients.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return records;
    }

    public async Task AuditAsync(string actor, string action, string? clientId, string? details, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AuditLog.Add(new AuditEntity
        {
            Timestamp = time.GetUtcNow().UtcDateTime,
            Actor = actor,
            Action = action,
            ClientId = clientId,
            Details = details?.Length > 2048 ? details[..2048] : details,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditRecord>> RecentAuditAsync(int max, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AuditLog.AsNoTracking().OrderByDescending(a => a.Id).Take(max)
            .Select(a => new AuditRecord(a.Timestamp, a.Actor, a.Action, a.ClientId, a.Details)).ToListAsync(ct);
    }
}

/// <summary>In-memory repository for running without MySQL (tests, quick local runs). Nothing survives a restart.</summary>
public sealed class InMemoryClientRepository(TimeProvider time) : IClientRepository
{
    private readonly ConcurrentDictionary<string, ClientRecord> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<AuditRecord> _audit = new();
    private int _nextSessionId;

    public Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClientRecord>>([.. _clients.Values.OrderBy(c => c.CreatedAt)]);

    public Task<ClientRecord?> GetAsync(string clientId, CancellationToken ct = default) =>
        Task.FromResult(_clients.GetValueOrDefault(clientId));

    public Task<(ClientRecord Client, SessionConfig Session)?> FindBySessionAsync(string beginString, string clientCompId,
        string venueCompId, CancellationToken ct = default)
    {
        foreach (var c in _clients.Values)
        {
            foreach (var s in c.Sessions)
            {
                if (s.BeginString == beginString && s.ClientCompId == clientCompId && s.VenueCompId == venueCompId)
                {
                    return Task.FromResult<(ClientRecord, SessionConfig)?>((c, s));
                }
            }
        }

        return Task.FromResult<(ClientRecord, SessionConfig)?>(null);
    }

    public Task CreateAsync(ClientRecord client, CancellationToken ct = default)
    {
        var sessions = client.Sessions.Select(s => s with { Id = Interlocked.Increment(ref _nextSessionId) }).ToList();
        if (!_clients.TryAdd(client.ClientId, client with { Sessions = sessions }))
        {
            throw new InvalidOperationException($"Client {client.ClientId} already exists");
        }

        return Task.CompletedTask;
    }

    public Task UpdateLimitsAsync(string clientId, RiskLimits limits, CancellationToken ct = default)
    {
        _clients.AddOrUpdate(clientId, _ => throw new KeyNotFoundException(clientId), (_, c) => c with { Limits = limits });
        return Task.CompletedTask;
    }

    public Task SetKillSwitchAsync(string clientId, bool engaged, CancellationToken ct = default)
    {
        _clients.AddOrUpdate(clientId, _ => throw new KeyNotFoundException(clientId), (_, c) => c with { KillSwitch = engaged });
        return Task.CompletedTask;
    }

    public Task<SessionConfig> AddSessionAsync(SessionConfig session, CancellationToken ct = default)
    {
        var saved = session with { Id = Interlocked.Increment(ref _nextSessionId) };
        _clients.AddOrUpdate(session.ClientId, _ => throw new KeyNotFoundException(session.ClientId),
            (_, c) => c with { Sessions = [.. c.Sessions, saved] });
        return Task.FromResult(saved);
    }

    public Task<IReadOnlyList<ClientRecord>> PurgeExpiredGuestsAsync(DateTime now, CancellationToken ct = default)
    {
        var removed = new List<ClientRecord>();
        foreach (var c in _clients.Values)
        {
            if (c.IsGuest && c.ExpiresAt < now && _clients.TryRemove(c.ClientId, out var r))
            {
                removed.Add(r);
            }
        }

        return Task.FromResult<IReadOnlyList<ClientRecord>>(removed);
    }

    public Task AuditAsync(string actor, string action, string? clientId, string? details, CancellationToken ct = default)
    {
        _audit.Enqueue(new AuditRecord(time.GetUtcNow().UtcDateTime, actor, action, clientId, details));
        while (_audit.Count > 1000 && _audit.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditRecord>> RecentAuditAsync(int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditRecord>>([.. _audit.Reverse().Take(max)]);
}
