using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Tickwire.Engine;
using Tickwire.Fix;
using Tickwire.Fix.Dictionary;
using Tickwire.Fix.Session;
using Tickwire.Fix.Session.Store;
using Tickwire.Fix.Session.Transport;
using Tickwire.Persistence;

namespace Tickwire.Api.Services;

public sealed record ManagedSession(string Key, FixSession Session, ClientAccount Account, SessionConfig Config);

public sealed record GuestProvisioned(string ClientId, string Token, string SessionKey, string ClientCompId, string VenueCompId,
    DateTime ExpiresAt);

/// <summary>
/// Owns every venue-side FIX session. Resolves incoming Logons against the client configuration in the database,
/// creates sessions on demand (with persistent sequence numbers when MySQL is configured) and provisions guests.
/// </summary>
public sealed class SessionManager : ISessionResolver, IAsyncDisposable
{
    private readonly IClientRepository _repo;
    private readonly FixOrderGateway _gateway;
    private readonly OrderManager _oms;
    private readonly WireTap _tap;
    private readonly TickwireOptions _options;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly ISessionStoreBackend? _backend;
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ClientAccount> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GuestTrader> _guests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly List<IAsyncDisposable> _stores = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SessionManager(IClientRepository repo, FixOrderGateway gateway, OrderManager oms, WireTap tap, IOptions<TickwireOptions> options,
        TimeProvider time, ILoggerFactory loggers, ISessionStoreBackend? backend = null)
    {
        _repo = repo;
        _gateway = gateway;
        _oms = oms;
        _tap = tap;
        _options = options.Value;
        _time = time;
        _loggers = loggers;
        _backend = backend;
    }

    /// <summary>Set by the host once the acceptor exists (guest traders connect through it).</summary>
    public FixAcceptor Acceptor { get; set; } = null!;

    public IEnumerable<ManagedSession> Sessions => _sessions.Values;

    public ManagedSession? Session(string key) => _sessions.GetValueOrDefault(key);

    public ClientAccount? Account(string clientId) => _accounts.GetValueOrDefault(clientId);

    public GuestTrader? Guest(string clientId) => _guests.GetValueOrDefault(clientId);

    public async ValueTask<SessionResolution> ResolveAsync(FixMessage logon, string remote, CancellationToken cancellationToken)
    {
        var found = await _repo.FindBySessionAsync(logon.BeginString, logon.SenderCompID, logon.TargetCompID, cancellationToken)
            .ConfigureAwait(false);
        if (found is null)
        {
            return SessionResolution.Reject(
                $"Unknown session {logon.SenderCompID}->{logon.TargetCompID}. Provision credentials on the Connect page first.");
        }

        var (client, config) = found.Value;
        if (config.Transport == "memory" && !remote.StartsWith("memory:", StringComparison.Ordinal))
        {
            return SessionResolution.Reject("This session belongs to the in-browser trader; provision a separate FIX session to connect");
        }

        if (client.ExpiresAt < _time.GetUtcNow().UtcDateTime)
        {
            return SessionResolution.Reject("Credentials have expired");
        }

        var managed = await GetOrCreateAsync(client, config, cancellationToken).ConfigureAwait(false);
        return new SessionResolution(managed.Session, null, config.EnableChaos);
    }

    public async Task<ManagedSession> GetOrCreateAsync(ClientRecord client, SessionConfig config, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(config.Key, out var existing))
        {
            return existing;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(config.Key, out existing))
            {
                return existing;
            }

            var account = _accounts.GetOrAdd(client.ClientId,
                _ => new ClientAccount(client.ClientId, client.DisplayName, client.Limits, client.IsGuest) { KillSwitch = client.KillSwitch });
            var id = new SessionId(config.BeginString, config.VenueCompId, config.ClientCompId);
            var (store, _) = await OpenStoreAsync(id, ct).ConfigureAwait(false);
            var session = new FixSession(new SessionSettings
            {
                Id = id,
                Role = SessionRole.Acceptor,
                HeartBtInt = config.HeartBtInt,
                Dictionary = FixDictionary.For(config.BeginString),
                DefaultApplVerID = config.BeginString == Fixt11 ? "9" : null,
            }, store, _gateway, _time, _loggers.CreateLogger("Tickwire.Session"), _tap.Observer(config.Key, "venue", isVenueSide: true));
            if (config.IsDropCopy)
            {
                _gateway.RegisterDropCopy(account, session);
            }
            else
            {
                _gateway.Register(account, session);
            }

            await session.StartAsync().ConfigureAwait(false);
            var managed = new ManagedSession(config.Key, session, account, config);
            _sessions[config.Key] = managed;
            return managed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GuestProvisioned> CreateGuestAsync(CancellationToken ct = default)
    {
        var suffix = RandomCode(6);
        var clientId = $"gst-{suffix.ToLowerInvariant()}";
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var now = _time.GetUtcNow().UtcDateTime;
        var expires = now.AddHours(_options.GuestTtlHours);
        var session = new SessionConfig(0, clientId, "FIX.4.4", $"GST-{suffix}", _options.VenueCompId, _options.GuestHeartBtInt, false,
            "memory", true);
        var record = new ClientRecord(clientId, $"Guest {suffix}", true, false, RiskLimits.Guest, [session], now, expires, Hash(token));
        await _repo.CreateAsync(record, ct).ConfigureAwait(false);
        await _repo.AuditAsync(clientId, "guest.created", clientId, $"expires {expires:O}", ct).ConfigureAwait(false);
        _tokens[Hash(token)] = clientId;
        await EnsureGuestTraderAsync(clientId, ct).ConfigureAwait(false);
        return new GuestProvisioned(clientId, token, session.Key, session.ClientCompId, session.VenueCompId, expires);
    }

    /// <summary>Starts the in-browser trader for a guest if it isn't running (e.g. after a server restart).</summary>
    public async Task<GuestTrader?> EnsureGuestTraderAsync(string clientId, CancellationToken ct = default)
    {
        if (_guests.TryGetValue(clientId, out var running))
        {
            return running;
        }

        var client = await _repo.GetAsync(clientId, ct).ConfigureAwait(false);
        var config = client?.Sessions.FirstOrDefault(s => s.Transport == "memory");
        if (client is null || config is null)
        {
            return null;
        }

        var managed = await GetOrCreateAsync(client, config, ct).ConfigureAwait(false);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_guests.TryGetValue(clientId, out running))
            {
                return running;
            }

            var (store, fresh) = await OpenStoreAsync(new SessionId(config.BeginString, config.ClientCompId, config.VenueCompId), ct)
                .ConfigureAwait(false);
            var trader = new GuestTrader(managed.Account, config, config.Key, Acceptor, _tap, store, fresh, _time,
                _loggers.CreateLogger("Tickwire.GuestTrader"));
            _guests[clientId] = trader;
            await trader.StartAsync().ConfigureAwait(false);
            return trader;
        }
        finally
        {
            _lock.Release();
        }
    }

    public const string Fixt11 = "FIXT.1.1";

    /// <summary>FIX 4.4, or FIXT 1.1 carrying FIX 5.0 SP2 (DefaultApplVerID 9) for external sessions.</summary>
    public static readonly IReadOnlyList<string> SupportedBeginStrings = ["FIX.4.4", Fixt11];

    /// <summary>
    /// Adds a TCP session to a client so they can connect their own FIX engine: a trading session (BYO-xxxxxx) or a
    /// receive-only drop copy (DC-xxxxxx). One of each per role and FIX version; asking again returns the same one.
    /// </summary>
    public async Task<SessionConfig> ProvisionExternalSessionAsync(string clientId, string role = SessionRoles.Trading,
        string beginString = "FIX.4.4", CancellationToken ct = default)
    {
        var client = await _repo.GetAsync(clientId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(clientId);
        var existing = client.Sessions.FirstOrDefault(s => s.Transport == "tcp" && s.Role == role && s.BeginString == beginString);
        if (existing is not null)
        {
            return existing;
        }

        var prefix = role == SessionRoles.DropCopy ? "DC" : "BYO";
        var config = await _repo.AddSessionAsync(new SessionConfig(0, clientId, beginString, $"{prefix}-{RandomCode(6)}", _options.VenueCompId,
            30, true, "tcp", false, role), ct).ConfigureAwait(false);
        await _repo.AuditAsync(clientId, "session.provisioned", clientId, config.Key, ct).ConfigureAwait(false);
        return config;
    }

    public async Task<string?> ClientIdForTokenAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var hash = Hash(token);
        if (_tokens.TryGetValue(hash, out var id))
        {
            return id;
        }

        var match = (await _repo.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(c => c.OwnerTokenHash == hash);
        if (match is not null)
        {
            _tokens[hash] = match.ClientId;
        }

        return match?.ClientId;
    }

    public async Task<ClientAccount?> LoadAccountAsync(string clientId, CancellationToken ct = default)
    {
        if (_accounts.TryGetValue(clientId, out var account))
        {
            return account;
        }

        var client = await _repo.GetAsync(clientId, ct).ConfigureAwait(false);
        return client is null
            ? null
            : _accounts.GetOrAdd(clientId, _ => new ClientAccount(client.ClientId, client.DisplayName, client.Limits, client.IsGuest)
            {
                KillSwitch = client.KillSwitch,
            });
    }

    public async Task RemoveClientAsync(ClientRecord client)
    {
        if (_guests.TryRemove(client.ClientId, out var trader))
        {
            await trader.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var config in client.Sessions)
        {
            if (_sessions.TryRemove(config.Key, out var managed))
            {
                managed.Session.Logout("Session removed");
                await Task.Delay(20).ConfigureAwait(false);
                await managed.Session.DisposeAsync().ConfigureAwait(false);
            }

            _tap.Forget(config.Key);
        }

        await _oms.CancelAllAsync(client.ClientId, "Client removed").ConfigureAwait(false);
        _gateway.Unregister(client.ClientId);
        _accounts.TryRemove(client.ClientId, out _);
        foreach (var kv in _tokens.Where(kv => kv.Value == client.ClientId).ToList())
        {
            _tokens.TryRemove(kv.Key, out _);
        }
    }

    private int _disposeState1;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState1, 1) != 0)
        {
            return;
        }

        foreach (var guest in _guests.Values)
        {
            await guest.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var s in _sessions.Values)
        {
            await s.Session.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var store in _stores)
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }

        _lock.Dispose();
    }

    private async Task<(ISessionStore Store, bool Fresh)> OpenStoreAsync(SessionId id, CancellationToken ct)
    {
        if (_backend is null)
        {
            return (new MemorySessionStore(_time.GetUtcNow().UtcDateTime), true);
        }

        var store = await WriteBehindSessionStore.OpenAsync(id, _backend, _time, _loggers.CreateLogger("Tickwire.SessionStore"), ct)
            .ConfigureAwait(false);
        lock (_stores)
        {
            _stores.Add(store);
        }

        return (store, false);
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string RandomCode(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
        });
    }
}
