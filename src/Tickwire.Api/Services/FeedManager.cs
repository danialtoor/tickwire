using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using Tickwire.MarketData;
using Tickwire.Venue;

namespace Tickwire.Api.Services;

public sealed record FeedStatusDto(
    string? Provider,
    string? ProviderName,
    string State,
    string? Message,
    DateTime? StartedAt,
    long Updates,
    int Contracts,
    DateTime? LastUpdate);

public sealed record FeedSnapshotDto(FeedStatusDto Status, IReadOnlyList<ExternalQuote> Quotes, IReadOnlyList<ExternalUnderlying> Underlyings);

/// <summary>
/// Runs at most one external market data connection per client. Credentials are held only by the running task's
/// closure: never persisted, never logged, and scrubbed from any error text shown back to the user. Quotes are merged
/// per contract (NBBO, implied vol and trades often arrive as separate messages) and pushed to that client only.
/// </summary>
public sealed partial class FeedManager : IAsyncDisposable
{
    private const int MaxConnections = 25;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(2);

    private readonly Dictionary<string, IOptionFeedProvider> _providers;
    private readonly InstrumentRegistry _instruments;
    private readonly LiveBus _bus;
    private readonly ILogger<FeedManager> _logger;
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _publisher;

    public FeedManager(IEnumerable<IOptionFeedProvider> providers, InstrumentRegistry instruments, LiveBus bus, ILogger<FeedManager> logger)
    {
        _providers = providers.ToDictionary(p => p.Info.Id, StringComparer.OrdinalIgnoreCase);
        _instruments = instruments;
        _bus = bus;
        _logger = logger;
        _publisher = Task.Run(PublishLoopAsync);
    }

    public static string Group(string clientId) => $"feed:{clientId}";

    public IReadOnlyList<ProviderInfo> Providers => [.. _providers.Values.Select(p => p.Info).OrderBy(i => i.Id == "demo" ? 1 : 0)];

    public FeedStatusDto Status(string clientId) =>
        _connections.TryGetValue(clientId, out var c) ? c.Status() : new FeedStatusDto(null, null, "Idle", null, null, 0, 0, null);

    public FeedSnapshotDto Snapshot(string clientId) =>
        _connections.TryGetValue(clientId, out var c)
            ? new FeedSnapshotDto(c.Status(), [.. c.Quotes.Values], [.. c.Underlyings.Values])
            : new FeedSnapshotDto(Status(clientId), [], []);

    public async Task<FeedStatusDto> ConnectAsync(string clientId, string providerId, IReadOnlyDictionary<string, string> credentials)
    {
        if (!_providers.TryGetValue(providerId, out var provider))
        {
            throw new ArgumentException($"Unknown provider '{providerId}'");
        }

        var missing = provider.Info.Credentials.Where(f => f.DefaultValue is null && string.IsNullOrWhiteSpace(credentials.GetValueOrDefault(f.Name)))
            .Select(f => f.Label).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"Missing {string.Join(", ", missing)}");
        }

        await DisconnectAsync(clientId).ConfigureAwait(false);
        if (_connections.Count >= MaxConnections)
        {
            throw new InvalidOperationException("Too many live feeds are running right now; try again later");
        }

        // Only the fields the provider declares are kept, with defaults filled in.
        var creds = provider.Info.Credentials.ToDictionary(f => f.Name,
            f => credentials.GetValueOrDefault(f.Name) is { Length: > 0 } v ? v.Trim() : f.DefaultValue ?? "", StringComparer.Ordinal);
        var subscription = new FeedSubscription([.. _instruments.Underlyings.Select(u => u.Symbol)], [.. _instruments.All.Select(c => c.Occ)]);
        var connection = new Connection(provider.Info, [.. creds.Values.Where(v => v.Length >= 4)], subscription.Contracts.Count);
        _connections[clientId] = connection;
        connection.Task = Task.Run(() => RunAsync(clientId, provider, creds, subscription, connection));
        return connection.Status();
    }

    public async Task DisconnectAsync(string clientId)
    {
        if (_connections.TryRemove(clientId, out var c))
        {
            await c.Cancel.CancelAsync().ConfigureAwait(false);
            try
            {
                await c.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }

            c.Cancel.Dispose();
            _bus.Publish(Group(clientId), "feedStatus", new FeedStatusDto(null, null, "Idle", "Disconnected", null, 0, 0, null));
        }
    }

    private async Task RunAsync(string clientId, IOptionFeedProvider provider, Dictionary<string, string> creds, FeedSubscription subscription,
        Connection c)
    {
        c.Cancel.CancelAfter(MaxDuration);
        c.Set("Connecting", $"Connecting to {provider.Info.Name}…");
        Publish(clientId, c);
        try
        {
            await provider.RunAsync(creds, subscription, c, c.Cancel.Token).ConfigureAwait(false);
            c.Set("Stopped", $"{provider.Info.Name} ended the stream");
        }
        catch (OperationCanceledException) when (c.Cancel.IsCancellationRequested)
        {
            c.Set("Stopped", c.StartedAt + MaxDuration <= DateTime.UtcNow ? "Stopped after the 2-hour session limit" : "Disconnected");
        }
        catch (FeedAuthException ex)
        {
            c.Set("Error", c.Redact(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or HttpRequestException)
        {
            c.Set("Error", c.Redact($"Couldn't stay connected to {provider.Info.Name}: {ex.Message}"));
        }
        catch (Exception ex)
        {
            FeedFailed(_logger, ex.GetType().Name, provider.Info.Id); // type only: the message could echo a credential
            c.Set("Error", $"{provider.Info.Name} adapter failed ({ex.GetType().Name})");
        }

        Publish(clientId, c);
    }

    private async Task PublishLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                foreach (var (clientId, c) in _connections)
                {
                    if (!c.Dirty.IsEmpty || c.StatusChanged)
                    {
                        Publish(clientId, c);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Publish(string clientId, Connection c)
    {
        var changed = new List<ExternalQuote>();
        foreach (var occ in c.Dirty.Keys)
        {
            if (c.Dirty.TryRemove(occ, out _) && c.Quotes.TryGetValue(occ, out var q))
            {
                changed.Add(q);
            }
        }

        c.StatusChanged = false;
        _bus.Publish(Group(clientId), "feedQuotes", new FeedSnapshotDto(c.Status(), changed, [.. c.Underlyings.Values]));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var id in _connections.Keys.ToList())
        {
            await DisconnectAsync(id).ConfigureAwait(false);
        }

        try
        {
            await _publisher.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feed adapter {Provider} failed with {ExceptionType}")]
    private static partial void FeedFailed(ILogger logger, string exceptionType, string provider);

    private sealed class Connection(ProviderInfo info, IReadOnlyList<string> secrets, int contracts) : IFeedSink
    {
        private readonly Lock _lock = new();
        private string _state = "Connecting";
        private string? _message;
        private long _updates;
        private DateTime? _lastUpdate;

        public CancellationTokenSource Cancel { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public ConcurrentDictionary<string, ExternalQuote> Quotes { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, byte> Dirty { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, ExternalUnderlying> Underlyings { get; } = new(StringComparer.Ordinal);
        public volatile bool StatusChanged;

        public void Set(string state, string? message)
        {
            lock (_lock)
            {
                _state = state;
                _message = message;
            }

            StatusChanged = true;
        }

        public FeedStatusDto Status()
        {
            lock (_lock)
            {
                return new FeedStatusDto(info.Id, info.Name, _state, _message, StartedAt, Interlocked.Read(ref _updates), contracts, _lastUpdate);
            }
        }

        public string Redact(string text) => secrets.Aggregate(text, (t, s) => t.Replace(s, "•••", StringComparison.Ordinal));

        public void OnQuote(ExternalQuote quote)
        {
            Quotes.AddOrUpdate(quote.OccSymbol, quote, (_, prev) => new ExternalQuote(quote.OccSymbol,
                quote.Bid ?? prev.Bid, quote.Ask ?? prev.Ask, quote.BidSize ?? prev.BidSize, quote.AskSize ?? prev.AskSize,
                quote.Last ?? prev.Last, quote.ImpliedVol ?? prev.ImpliedVol, quote.Delta ?? prev.Delta,
                quote.Time > prev.Time ? quote.Time : prev.Time, quote.Provider));
            Dirty[quote.OccSymbol] = 0;
            MarkUpdate();
        }

        public void OnUnderlying(ExternalUnderlying underlying)
        {
            Underlyings[underlying.Symbol] = underlying;
            MarkUpdate();
        }

        public void OnStatus(string message) => Set("Streaming", Redact(message));

        private void MarkUpdate()
        {
            Interlocked.Increment(ref _updates);
            lock (_lock)
            {
                _lastUpdate = DateTime.UtcNow;
                if (_state == "Connecting")
                {
                    _state = "Streaming";
                    StatusChanged = true;
                }
            }
        }
    }
}
