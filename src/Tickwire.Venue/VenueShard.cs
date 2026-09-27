using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickwire.Pricing;

namespace Tickwire.Venue;

public abstract record VenueEvent(long OrderId);

/// <summary>Result of a new order: immediate fills, what rests, and what was canceled (IOC/FOK/market remainder).</summary>
public sealed record OrderSubmitted(long OrderId, IReadOnlyList<Fill> Fills, decimal RestingQuantity, decimal CanceledQuantity,
    string? CancelReason) : VenueEvent(OrderId);

/// <summary>A resting order traded against an incoming one.</summary>
public sealed record PassiveFill(long OrderId, decimal Price, decimal Quantity) : VenueEvent(OrderId);

public sealed record OrderCanceled(long OrderId, decimal CanceledQuantity) : VenueEvent(OrderId);

public sealed record CancelFailed(long OrderId, string Reason) : VenueEvent(OrderId);

public sealed record OrderReplaced(long OrderId, IReadOnlyList<Fill> Fills, decimal RestingQuantity) : VenueEvent(OrderId);

public sealed record ReplaceFailed(long OrderId, string Reason) : VenueEvent(OrderId);

/// <summary>Receives venue events for client orders (positive ids). Called from shard loops; must be thread-safe.</summary>
public interface IVenueEventSink
{
    void OnVenueEvent(VenueEvent e);
}

public sealed record VenueOptions
{
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public double RiskFreeRate { get; init; } = 0.045;

    /// <summary>Simulated market time per real second. Makes the underlying move visibly during a short demo.</summary>
    public double TimeAcceleration { get; init; } = 20;

    public bool EnableMarketMakers { get; init; } = true;
    public bool EnableNoiseTraders { get; init; } = true;
    public int Seed { get; init; } = 42;
}

/// <summary>
/// Everything for one underlying: its price process, its option books, the market-maker bots quoting them and a noise
/// trader that creates tape. One loop per underlying, fed by a channel; nothing inside is shared across threads.
/// See ADR 0003 for why the books are sharded by underlying.
/// </summary>
public sealed partial class VenueShard : IAsyncDisposable
{
    private static readonly MarketMakerProfile[] Makers =
    [
        new("MM-ALPHA", HalfSpreadPct: 0.03, MinHalfSpread: 0.02m, MinSize: 5, MaxSize: 20),
        new("MM-BETA", HalfSpreadPct: 0.06, MinHalfSpread: 0.05m, MinSize: 20, MaxSize: 60),
    ];

    private readonly UnderlyingSpec _spec;
    private readonly VenueOptions _options;
    private readonly MarketDataCache _cache;
    private readonly Func<IVenueEventSink?> _sink;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Random _random;
    private readonly VolSurface _surface;
    private readonly Dictionary<int, OrderBook> _books;
    private readonly Dictionary<long, int> _orderContract = [];
    private readonly Dictionary<long, decimal> _clientFilled = [];
    private readonly Dictionary<long, (int ContractId, int Maker)> _botOrders = [];
    private readonly Dictionary<(int ContractId, int Maker), (long Bid, long Ask, decimal BidPx, decimal AskPx)> _botQuotes = [];
    private readonly Dictionary<int, Greeks> _theo = [];
    private readonly HashSet<int> _dirty = [];
    private readonly Channel<Func<ValueTask>> _commands = Channel.CreateUnbounded<Func<ValueTask>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private Task? _loop;
    private long _nextBotOrderId = -1;
    private double _spot;
    private DateTime _lastTick;

    public VenueShard(UnderlyingSpec spec, IEnumerable<OptionContract> contracts, VenueOptions options, MarketDataCache cache,
        Func<IVenueEventSink?> sink, TimeProvider? time = null, ILogger? logger = null)
    {
        _spec = spec;
        _options = options;
        _cache = cache;
        _sink = sink;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _random = new Random(HashCode.Combine(options.Seed, spec.Symbol.Length, spec.Symbol[0]));
        _surface = new VolSurface(spec.AtmImpliedVol);
        _books = contracts.ToDictionary(c => c.Id, c => new OrderBook(c));
        _spot = spec.InitialPrice;
    }

    public string Underlying => _spec.Symbol;
    public double Spot => _spot;

    public void Start(bool withTimer = true)
    {
        _lastTick = Now;
        _cache.Publish(new UnderlyingSnapshot(_spec.Symbol, _spot, _spec.InitialPrice, Now));
        Post(() =>
        {
            RepriceAndRequote();
            PublishDirty();
            return ValueTask.CompletedTask;
        });
        _loop ??= Task.Run(RunAsync);
        if (withTimer)
        {
            _timer = _time.CreateTimer(_ => Post(TickAsync), null, _options.TickInterval, _options.TickInterval);
        }
    }

    public void Submit(long orderId, int contractId, Side side, OrderType type, TimeInForce tif, decimal? price, decimal quantity) =>
        Post(() =>
        {
            var book = _books[contractId];
            var result = book.Submit(orderId, side, type, tif, price, quantity);
            if (result.RestingQuantity > 0)
            {
                _orderContract[orderId] = contractId;
                _clientFilled[orderId] = result.FilledQuantity;
            }

            Emit(new OrderSubmitted(orderId, result.Fills, result.RestingQuantity, result.CanceledQuantity, result.CancelReason));
            AfterFills(book, side, result.Fills);
            return ValueTask.CompletedTask;
        });

    public void Cancel(long orderId) => Post(() =>
    {
        if (_orderContract.Remove(orderId, out var contractId) && _books[contractId].Cancel(orderId) is { } qty)
        {
            _clientFilled.Remove(orderId);
            _dirty.Add(contractId);
            Emit(new OrderCanceled(orderId, qty));
        }
        else
        {
            Emit(new CancelFailed(orderId, "Order is not open at the venue"));
        }

        return ValueTask.CompletedTask;
    });

    /// <summary>
    /// Changes an open order's price and total quantity. The shard knows how much has filled so far (including fills
    /// that happened after the client sent the replace), so it computes the new open quantity itself and can never overfill.
    /// </summary>
    public void Replace(long orderId, decimal newPrice, decimal newTotalQuantity) => Post(() =>
    {
        if (!_orderContract.TryGetValue(orderId, out var contractId))
        {
            Emit(new ReplaceFailed(orderId, "Order is not open at the venue"));
            return ValueTask.CompletedTask;
        }

        var filled = _clientFilled.GetValueOrDefault(orderId);
        var newRemaining = newTotalQuantity - filled;
        if (newRemaining <= 0)
        {
            Emit(new ReplaceFailed(orderId, $"New quantity {newTotalQuantity} is not above the filled quantity {filled}"));
            return ValueTask.CompletedTask;
        }

        var book = _books[contractId];
        var side = book.Get(orderId)!.Side;
        var result = book.Replace(orderId, newPrice, newRemaining)!.Value;
        if (result.RestingQuantity == 0)
        {
            _orderContract.Remove(orderId);
            _clientFilled.Remove(orderId);
        }
        else
        {
            _clientFilled[orderId] = filled + result.FilledQuantity;
        }

        Emit(new OrderReplaced(orderId, result.Fills, result.RestingQuantity));
        AfterFills(book, side, result.Fills);
        return ValueTask.CompletedTask;
    });

    /// <summary>Moves the underlying by a percentage (ops/testing: "flash move").</summary>
    public void Shock(double percent) => Post(() =>
    {
        _spot *= 1 + (percent / 100);
        RepriceAndRequote();
        PublishDirty();
        return ValueTask.CompletedTask;
    });

    /// <summary>Runs one market tick now (tests drive the shard this way instead of with the timer).</summary>
    public void Tick() => Post(TickAsync);

    /// <summary>Completes once everything posted before this call has run.</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            done.TrySetResult();
            return ValueTask.CompletedTask;
        });
        return done.Task;
    }

    /// <summary>Runs <paramref name="read"/> on the shard loop, for consistent reads of book state.</summary>
    public Task<T> ReadAsync<T>(Func<IReadOnlyDictionary<int, OrderBook>, T> read)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            done.TrySetResult(read(_books));
            return ValueTask.CompletedTask;
        });
        return done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

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

        _stop.Dispose();
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private void Post(Func<ValueTask> work) => _commands.Writer.TryWrite(work);

    private async Task RunAsync()
    {
        var reader = _commands.Reader;
        while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
        {
            while (reader.TryRead(out var work))
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ShardError(_logger, ex, _spec.Symbol);
                }
            }
        }
    }

    private ValueTask TickAsync()
    {
        var now = Now;
        var dtYears = Math.Max(0, (now - _lastTick).TotalSeconds) * _options.TimeAcceleration / (252 * 6.5 * 3600);
        _lastTick = now;
        if (dtYears > 0)
        {
            _spot = Gbm.Step(_spot, 0, _spec.RealizedVol, dtYears, Gbm.NextGaussian(_random));
        }

        RepriceAndRequote();
        if (_options.EnableNoiseTraders && _random.NextDouble() < 0.35)
        {
            NoiseTrade();
        }

        PublishDirty();
        return ValueTask.CompletedTask;
    }

    private void RepriceAndRequote()
    {
        _cache.Publish(new UnderlyingSnapshot(_spec.Symbol, _spot, _spec.InitialPrice, Now));
        foreach (var book in _books.Values)
        {
            var c = book.Contract;
            var t = YearsToExpiry(c.Expiry);
            var strike = (double)c.Strike;
            var vol = _surface.Vol(_spot, strike, t, _options.RiskFreeRate, _spec.DividendYield);
            _theo[c.Id] = BlackScholes.Compute(c.Right, _spot, strike, t, _options.RiskFreeRate, _spec.DividendYield, vol);
            if (_options.EnableMarketMakers)
            {
                Requote(book);
            }

            _dirty.Add(c.Id);
        }
    }

    private void Requote(OrderBook book)
    {
        var theo = (decimal)_theo[book.Contract.Id].Price;
        var tick = book.Contract.TickSize;

        // Decide every maker's new quotes first, then pull and re-post them all, so makers never trade with each other.
        var desired = new (decimal Bid, decimal Ask)[Makers.Length];
        var changed = false;
        for (var m = 0; m < Makers.Length; m++)
        {
            var p = Makers[m];
            var half = Math.Max(p.MinHalfSpread, theo * (decimal)p.HalfSpreadPct);
            var bid = Math.Floor((theo - half) / tick) * tick;
            var ask = Math.Max(Math.Ceiling((theo + half) / tick) * tick, bid + tick);
            desired[m] = (bid, ask);

            if (!_botQuotes.TryGetValue((book.Contract.Id, m), out var q)
                || q.BidPx != bid || q.AskPx != ask
                || (q.Bid != 0 && !book.Contains(q.Bid) && bid >= tick) || (q.Ask != 0 && !book.Contains(q.Ask)))
            {
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        for (var m = 0; m < Makers.Length; m++)
        {
            if (_botQuotes.Remove((book.Contract.Id, m), out var q))
            {
                RemoveBotOrder(book, q.Bid);
                RemoveBotOrder(book, q.Ask);
            }
        }

        for (var m = 0; m < Makers.Length; m++)
        {
            var p = Makers[m];
            var (bidPx, askPx) = desired[m];
            long bidId = 0, askId = 0;
            if (bidPx >= tick)
            {
                bidId = PostBotOrder(book, m, Side.Buy, bidPx, _random.Next(p.MinSize, p.MaxSize + 1));
            }

            askId = PostBotOrder(book, m, Side.Sell, askPx, _random.Next(p.MinSize, p.MaxSize + 1));
            _botQuotes[(book.Contract.Id, m)] = (bidId, askId, bidPx, askPx);
        }
    }

    private long PostBotOrder(OrderBook book, int maker, Side side, decimal price, int size)
    {
        var id = _nextBotOrderId--;
        // A maker's new quote can cross a resting client order (the market moved through it): that trades.
        var result = book.Submit(id, side, OrderType.Limit, TimeInForce.Day, price, size);
        if (result.RestingQuantity > 0)
        {
            _botOrders[id] = (book.Contract.Id, maker);
        }

        AfterFills(book, side, result.Fills);
        return result.RestingQuantity > 0 ? id : 0;
    }

    private void RemoveBotOrder(OrderBook book, long id)
    {
        if (id != 0 && _botOrders.Remove(id))
        {
            book.Cancel(id);
        }
    }

    /// <summary>Occasionally lifts or hits a near-the-money quote so there is tape and resting client orders get filled.</summary>
    private void NoiseTrade()
    {
        var nearest = _books.Values.Min(x => x.Contract.Expiry).AddDays(14);
        var candidates = _books.Values
            .Where(b => Math.Abs((double)b.Contract.Strike - _spot) / _spot < 0.03 && b.Contract.Expiry <= nearest)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var book = candidates[_random.Next(candidates.Count)];
        var side = _random.Next(2) == 0 ? Side.Buy : Side.Sell;
        var top = side == Side.Buy ? book.BestAsk : book.BestBid;
        if (top is null)
        {
            return;
        }

        var id = _nextBotOrderId--;
        var result = book.Submit(id, side, OrderType.Limit, TimeInForce.ImmediateOrCancel, top.Value.Price, _random.Next(1, 6));
        AfterFills(book, side, result.Fills);
    }

    private void AfterFills(OrderBook book, Side aggressorSide, IReadOnlyList<Fill> fills)
    {
        _dirty.Add(book.Contract.Id);
        foreach (var f in fills)
        {
            _cache.Publish(new TradePrint(book.Contract.Id, _spec.Symbol, f.Price, f.Quantity, aggressorSide, Now));
            if (f.RestingId > 0)
            {
                if (book.Get(f.RestingId) is null)
                {
                    _orderContract.Remove(f.RestingId);
                    _clientFilled.Remove(f.RestingId);
                }
                else
                {
                    _clientFilled[f.RestingId] = _clientFilled.GetValueOrDefault(f.RestingId) + f.Quantity;
                }

                Emit(new PassiveFill(f.RestingId, f.Price, f.Quantity));
            }
            else if (book.Get(f.RestingId) is null)
            {
                _botOrders.Remove(f.RestingId);
            }
        }
    }

    private void PublishDirty()
    {
        var now = Now;
        foreach (var id in _dirty)
        {
            var book = _books[id];
            var g = _theo.GetValueOrDefault(id);
            var bid = book.BestBid;
            var ask = book.BestAsk;
            var c = book.Contract;
            var t = YearsToExpiry(c.Expiry);
            var iv = _surface.Vol(_spot, (double)c.Strike, t, _options.RiskFreeRate, _spec.DividendYield);
            _cache.Publish(new QuoteSnapshot(id, g.Price, iv, g.Delta, g.Gamma, g.Vega / 100, g.Theta / 365,
                bid?.Price, bid?.Quantity ?? 0, ask?.Price, ask?.Quantity ?? 0, book.LastPrice, book.Volume,
                book.Depth(Side.Buy, 5), book.Depth(Side.Sell, 5), now));
        }

        _dirty.Clear();
    }

    private void Emit(VenueEvent e) => _sink()?.OnVenueEvent(e);

    private double YearsToExpiry(DateOnly expiry)
    {
        // Options stop trading at 16:00 New York time on expiry day; 20:00 UTC is close enough for a simulation.
        var close = expiry.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);
        return Math.Max((close - Now).TotalDays / 365.0, 1.0 / (365 * 24));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Venue shard {Underlying} error")]
    private static partial void ShardError(ILogger logger, Exception ex, string underlying);

    private sealed record MarketMakerProfile(string Name, double HalfSpreadPct, decimal MinHalfSpread, int MinSize, int MaxSize);
}

/// <summary>The simulated exchange: routes each order to the shard that owns its underlying.</summary>
public sealed class SimulatedVenue : IAsyncDisposable
{
    private readonly Dictionary<string, VenueShard> _shards;
    private IVenueEventSink? _sink;

    public SimulatedVenue(InstrumentRegistry instruments, VenueOptions options, MarketDataCache cache, TimeProvider? time = null,
        ILoggerFactory? loggers = null)
    {
        Instruments = instruments;
        Cache = cache;
        var logger = loggers?.CreateLogger<VenueShard>();
        _shards = instruments.Underlyings.ToDictionary(u => u.Symbol,
            u => new VenueShard(u, instruments.Chain(u.Symbol), options, cache, () => _sink, time, logger),
            StringComparer.OrdinalIgnoreCase);
    }

    public InstrumentRegistry Instruments { get; }
    public MarketDataCache Cache { get; }
    public IReadOnlyCollection<VenueShard> Shards => _shards.Values;

    public void SetSink(IVenueEventSink sink) => _sink = sink;

    public void Start(bool withTimers = true)
    {
        foreach (var shard in _shards.Values)
        {
            shard.Start(withTimers);
        }
    }

    public VenueShard ShardFor(OptionContract contract) => _shards[contract.Underlying];

    public VenueShard? Shard(string underlying) => _shards.GetValueOrDefault(underlying);

    public void Submit(long orderId, OptionContract contract, Side side, OrderType type, TimeInForce tif, decimal? price, decimal quantity) =>
        ShardFor(contract).Submit(orderId, contract.Id, side, type, tif, price, quantity);

    public void Cancel(long orderId, OptionContract contract) => ShardFor(contract).Cancel(orderId);

    public void Replace(long orderId, OptionContract contract, decimal price, decimal newTotalQuantity) =>
        ShardFor(contract).Replace(orderId, price, newTotalQuantity);

    public Task FlushAsync() => Task.WhenAll(_shards.Values.Select(s => s.FlushAsync()));

    public async ValueTask DisposeAsync()
    {
        foreach (var shard in _shards.Values)
        {
            await shard.DisposeAsync().ConfigureAwait(false);
        }
    }
}
