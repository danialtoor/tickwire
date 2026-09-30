using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickwire.Pricing;

namespace Tickwire.Venue;

public abstract record VenueEvent(long OrderId);

/// <summary>Result of a new order: immediate fills, what rests, and what was canceled (IOC/FOK/market remainder).</summary>
public sealed record OrderSubmitted(long OrderId, IReadOnlyList<Fill> Fills, decimal RestingQuantity, decimal CanceledQuantity,
    string? CancelReason) : VenueEvent(OrderId)
{
    /// <summary>The exchange the order went to, and why (the router's explanation, or "Directed to ...").</summary>
    public string Exchange { get; init; } = Exchanges.Primary.Code;

    public string? RouteReason { get; init; }
}

/// <summary>A resting order traded against an incoming one.</summary>
public sealed record PassiveFill(long OrderId, decimal Price, decimal Quantity) : VenueEvent(OrderId)
{
    public string Exchange { get; init; } = Exchanges.Primary.Code;
}

public sealed record OrderCanceled(long OrderId, decimal CanceledQuantity) : VenueEvent(OrderId);

public sealed record CancelFailed(long OrderId, string Reason) : VenueEvent(OrderId);

public sealed record OrderReplaced(long OrderId, IReadOnlyList<Fill> Fills, decimal RestingQuantity) : VenueEvent(OrderId);

public sealed record ReplaceFailed(long OrderId, string Reason) : VenueEvent(OrderId);

/// <summary>One leg of a spread: a contract, how many of it per spread unit, and its side when the spread is bought.</summary>
public sealed record SpreadLeg(int ContractId, int Ratio, Side Side);

public sealed record LegFill(int ContractId, Side Side, decimal Price, decimal Quantity)
{
    public string Exchange { get; init; } = Exchanges.Primary.Code;
}

/// <summary>Spread units executed together, at a net strategy price, with the leg trades that made them up.</summary>
public sealed record SpreadExecution(decimal Units, decimal StrategyPrice, IReadOnlyList<LegFill> Legs);

public sealed record SpreadSubmitted(long OrderId, IReadOnlyList<SpreadExecution> Executions, decimal RestingUnits, decimal CanceledUnits,
    string? CancelReason) : VenueEvent(OrderId);

public sealed record SpreadFilled(long OrderId, SpreadExecution Execution) : VenueEvent(OrderId);

/// <summary>A resting client order was removed because its contract expired and was delisted.</summary>
public sealed record OrderExpired(long OrderId, decimal ExpiredQuantity) : VenueEvent(OrderId);

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

    /// <summary>Pull of the underlying back toward its starting price, per year (0 = pure random walk).</summary>
    public double MeanReversion { get; init; } = 12;

    public bool EnableMarketMakers { get; init; } = true;

    /// <summary>The exchanges to run, primary first. Every contract has a book on each.</summary>
    public IReadOnlyList<Exchange> Exchanges { get; init; } = Venue.Exchanges.All;
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
    private readonly UnderlyingSpec _spec;
    private readonly VenueOptions _options;
    private readonly MarketDataCache _cache;
    private readonly Func<IVenueEventSink?> _sink;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Random _random;
    private readonly VolSurface _surface;
    private readonly IReadOnlyList<Exchange> _exchanges;

    /// <summary>Books per exchange, indexed like <see cref="_exchanges"/>. <see cref="_books"/> is the primary's.</summary>
    private readonly Dictionary<int, OrderBook>[] _venues;
    private readonly Dictionary<int, OrderBook> _books;
    private readonly (int Venue, MakerProfile Profile)[] _makers;
    private readonly Dictionary<int, (decimal Price, decimal Volume)> _tape = [];
    private readonly Dictionary<long, int> _orderContract = [];
    private readonly Dictionary<long, int> _orderVenue = [];
    private readonly Dictionary<long, decimal> _clientFilled = [];
    private readonly Dictionary<long, (int ContractId, int Maker)> _botOrders = [];
    private readonly Dictionary<(int ContractId, int Maker), (long Bid, long Ask, decimal BidPx, decimal AskPx)> _botQuotes = [];
    private readonly Dictionary<int, Greeks> _theo = [];
    private readonly HashSet<int> _dirty = [];
    private readonly List<RestingSpread> _spreads = [];
    private long _nextLegOrderId = -1_000_000_000_000;
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
        _exchanges = options.Exchanges;
        var list = contracts.ToList();
        _venues = [.. _exchanges.Select(e => list.ToDictionary(c => c.Id, c => new OrderBook(c, e.Code)))];
        _books = _venues[0];
        _makers = [.. _exchanges.SelectMany((e, i) => e.Makers.Select(m => (i, m)))];
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

    /// <summary>
    /// A new order. With a <paramref name="destination"/> exchange code it goes there; otherwise the smart order router
    /// picks the exchange from every book's current top (see <see cref="OrderRouter"/>). Routing happens on the shard loop,
    /// so the router sees exactly the books the order will meet.
    /// </summary>
    public void Submit(long orderId, int contractId, Side side, OrderType type, TimeInForce tif, decimal? price, decimal quantity,
        string? destination = null) =>
        Post(() =>
        {
            int venue;
            string reason;
            var directed = destination is null ? -1 : IndexOf(destination);
            if (directed >= 0)
            {
                venue = directed;
                reason = $"Directed to {_exchanges[venue].Code} (ExDestination)";
            }
            else
            {
                var tops = _venues.Select((v, i) => new VenueTop(_exchanges[i], v[contractId].BestBid, v[contractId].BestAsk)).ToList();
                var decision = OrderRouter.Route(tops, side, type, price);
                venue = IndexOf(decision.Exchange.Code);
                reason = decision.Reason;
            }

            var book = _venues[venue][contractId];
            var result = book.Submit(orderId, side, type, tif, price, quantity);
            if (result.RestingQuantity > 0)
            {
                _orderContract[orderId] = contractId;
                _orderVenue[orderId] = venue;
                _clientFilled[orderId] = result.FilledQuantity;
            }

            Emit(new OrderSubmitted(orderId, result.Fills, result.RestingQuantity, result.CanceledQuantity, result.CancelReason)
            {
                Exchange = book.Exchange,
                RouteReason = reason,
            });
            AfterFills(book, side, result.Fills);
            return ValueTask.CompletedTask;
        });

    /// <summary>
    /// A multi-leg order. It trades against the outright books: whenever every leg's top of book supports at least one
    /// spread unit at a net price within the limit, all legs execute together (never one without the others). What
    /// can't trade immediately rests (Day) or is canceled (IOC) and is re-checked after every market update.
    /// The strategy price is Σ ±ratio × leg price in the legs' own direction: buying pays at most the limit, selling
    /// receives at least it.
    /// </summary>
    public void SubmitSpread(long orderId, IReadOnlyList<SpreadLeg> legs, Side side, TimeInForce tif, decimal limit, decimal units) =>
        Post(() =>
        {
            var spread = new RestingSpread(orderId, legs, side, limit, units);
            var executions = Execute(spread);
            var remaining = spread.Remaining;
            string? reason = null;
            decimal canceled = 0;
            if (remaining > 0 && tif != TimeInForce.Day)
            {
                canceled = remaining;
                reason = "Immediate-or-cancel spread remainder canceled";
            }
            else if (remaining > 0)
            {
                _spreads.Add(spread);
            }

            Emit(new SpreadSubmitted(orderId, executions, canceled > 0 ? 0 : remaining, canceled, reason));
            return ValueTask.CompletedTask;
        });

    public void Cancel(long orderId) => Post(() =>
    {
        var spreadIndex = _spreads.FindIndex(s => s.OrderId == orderId);
        if (spreadIndex >= 0)
        {
            Emit(new OrderCanceled(orderId, _spreads[spreadIndex].Remaining));
            _spreads.RemoveAt(spreadIndex);
            return ValueTask.CompletedTask;
        }

        if (_orderContract.Remove(orderId, out var contractId) && _orderVenue.Remove(orderId, out var venue)
            && _venues[venue][contractId].Cancel(orderId) is { } qty)
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
        if (_spreads.Exists(s => s.OrderId == orderId))
        {
            Emit(new ReplaceFailed(orderId, "Multi-leg orders can't be replaced; cancel and send a new one"));
            return ValueTask.CompletedTask;
        }

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

        var book = _venues[_orderVenue[orderId]][contractId];
        var side = book.Get(orderId)!.Side;
        var result = book.Replace(orderId, newPrice, newRemaining)!.Value;
        if (result.RestingQuantity == 0)
        {
            _orderContract.Remove(orderId);
            _orderVenue.Remove(orderId);
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

    /// <summary>Adds books for newly listed contracts; market makers start quoting them on the same step.</summary>
    public void List(IEnumerable<OptionContract> contracts)
    {
        var added = contracts.Where(c => c.Underlying == _spec.Symbol).ToList();
        Post(() =>
        {
            foreach (var c in added)
            {
                for (var v = 0; v < _venues.Length; v++)
                {
                    _venues[v].TryAdd(c.Id, new OrderBook(c, _exchanges[v].Code));
                }
            }

            RepriceAndRequote();
            PublishDirty();
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>
    /// Removes an expired expiry: resting client orders are reported as expired, maker quotes are pulled, and the books
    /// and their market data disappear.
    /// </summary>
    public void Delist(DateOnly expiry) => Post(() =>
    {
        foreach (var spread in _spreads.Where(s => s.Legs.Any(l => _books.TryGetValue(l.ContractId, out var b) && b.Contract.Expiry == expiry)).ToList())
        {
            Emit(new OrderExpired(spread.OrderId, spread.Remaining));
            _spreads.Remove(spread);
        }

        foreach (var book in _books.Values.Where(b => b.Contract.Expiry == expiry).ToList())
        {
            var id = book.Contract.Id;
            foreach (var (orderId, _) in _orderContract.Where(kv => kv.Value == id).ToList())
            {
                if (_venues[_orderVenue[orderId]][id].Cancel(orderId) is { } qty)
                {
                    Emit(new OrderExpired(orderId, qty));
                }

                _orderContract.Remove(orderId);
                _orderVenue.Remove(orderId);
                _clientFilled.Remove(orderId);
            }

            foreach (var (botId, _) in _botOrders.Where(kv => kv.Value.ContractId == id).ToList())
            {
                _botOrders.Remove(botId);
            }

            foreach (var key in _botQuotes.Keys.Where(k => k.ContractId == id).ToList())
            {
                _botQuotes.Remove(key);
            }

            foreach (var venue in _venues)
            {
                venue.Remove(id);
            }

            _tape.Remove(id);
            _theo.Remove(id);
            _dirty.Remove(id);
            _cache.Remove(id);
        }

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

    private int _disposeState1;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState1, 1) != 0)
        {
            return;
        }

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
            _spot = _options.MeanReversion > 0
                ? Gbm.MeanRevertingStep(_spot, _spec.InitialPrice, _options.MeanReversion, _spec.RealizedVol, dtYears, Gbm.NextGaussian(_random))
                : Gbm.Step(_spot, 0, _spec.RealizedVol, dtYears, Gbm.NextGaussian(_random));
        }

        RepriceAndRequote();
        if (_options.EnableNoiseTraders && _random.NextDouble() < 0.35)
        {
            NoiseTrade();
        }

        CheckRestingSpreads();

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
                Requote(c.Id);
            }

            _dirty.Add(c.Id);
        }
    }

    private void Requote(int contractId)
    {
        var theo = (decimal)_theo[contractId].Price;
        var tick = _books[contractId].Contract.TickSize;

        // Decide every maker's new quotes first, then pull and re-post them all, so makers never trade with each other.
        // Each maker quotes on its own exchange's book.
        var desired = new (decimal Bid, decimal Ask)[_makers.Length];
        var changed = false;
        for (var m = 0; m < _makers.Length; m++)
        {
            var (venue, p) = _makers[m];
            var book = _venues[venue][contractId];
            var half = Math.Max(p.MinHalfSpread, theo * (decimal)p.HalfSpreadPct);
            var bid = Math.Floor((theo - half) / tick) * tick;
            var ask = Math.Max(Math.Ceiling((theo + half) / tick) * tick, bid + tick);
            desired[m] = (bid, ask);

            if (!_botQuotes.TryGetValue((contractId, m), out var q)
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

        for (var m = 0; m < _makers.Length; m++)
        {
            if (_botQuotes.Remove((contractId, m), out var q))
            {
                var book = _venues[_makers[m].Venue][contractId];
                RemoveBotOrder(book, q.Bid);
                RemoveBotOrder(book, q.Ask);
            }
        }

        for (var m = 0; m < _makers.Length; m++)
        {
            var (venue, p) = _makers[m];
            var book = _venues[venue][contractId];
            var (bidPx, askPx) = desired[m];
            long bidId = 0, askId = 0;
            if (bidPx >= tick)
            {
                bidId = PostBotOrder(book, m, Side.Buy, bidPx, _random.Next(p.MinSize, p.MaxSize + 1));
            }

            askId = PostBotOrder(book, m, Side.Sell, askPx, _random.Next(p.MinSize, p.MaxSize + 1));
            _botQuotes[(contractId, m)] = (bidId, askId, bidPx, askPx);
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

        var contractId = candidates[_random.Next(candidates.Count)].Contract.Id;
        var side = _random.Next(2) == 0 ? Side.Buy : Side.Sell;

        // Takers go to the best displayed price, wherever it is, so a resting client order at the NBBO gets hit.
        var book = _venues.Select(v => v[contractId])
            .Where(b => (side == Side.Buy ? b.BestAsk : b.BestBid) is not null)
            .OrderBy(b => side == Side.Buy ? b.BestAsk!.Value.Price : -b.BestBid!.Value.Price)
            .FirstOrDefault();
        var top = side == Side.Buy ? book?.BestAsk : book?.BestBid;
        if (book is null || top is null)
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
            _cache.Publish(new TradePrint(book.Contract.Id, _spec.Symbol, f.Price, f.Quantity, aggressorSide, Now) { Exchange = book.Exchange });
            var tape = _tape.GetValueOrDefault(book.Contract.Id);
            _tape[book.Contract.Id] = (f.Price, tape.Volume + f.Quantity);
            if (f.RestingId > 0)
            {
                if (book.Get(f.RestingId) is null)
                {
                    _orderContract.Remove(f.RestingId);
                    _orderVenue.Remove(f.RestingId);
                    _clientFilled.Remove(f.RestingId);
                }
                else
                {
                    _clientFilled[f.RestingId] = _clientFilled.GetValueOrDefault(f.RestingId) + f.Quantity;
                }

                Emit(new PassiveFill(f.RestingId, f.Price, f.Quantity) { Exchange = book.Exchange });
            }
            else if (book.Get(f.RestingId) is null)
            {
                _botOrders.Remove(f.RestingId);
            }
        }
    }

    private void CheckRestingSpreads()
    {
        foreach (var spread in _spreads.ToList())
        {
            foreach (var execution in Execute(spread))
            {
                Emit(new SpreadFilled(spread.OrderId, execution));
            }

            if (spread.Remaining == 0)
            {
                _spreads.Remove(spread);
            }
        }
    }

    /// <summary>Executes as many whole spread units as the legs' top-of-book prices and sizes allow within the limit.</summary>
    private List<SpreadExecution> Execute(RestingSpread spread)
    {
        var executions = new List<SpreadExecution>();
        while (spread.Remaining > 0)
        {
            // Each leg takes the best displayed price across the exchanges (bigger size breaks a tie).
            var tops = new List<(OrderBook Book, SpreadLeg Leg, Side Side, BookLevel Level)>();
            foreach (var leg in spread.Legs)
            {
                if (!_books.ContainsKey(leg.ContractId))
                {
                    return executions;
                }

                var side = spread.Side == Side.Buy ? leg.Side : (leg.Side == Side.Buy ? Side.Sell : Side.Buy);
                var best = _venues.Select(v => v[leg.ContractId])
                    .Select(b => (Book: b, Level: side == Side.Buy ? b.BestAsk : b.BestBid))
                    .Where(x => x.Level is not null)
                    .OrderBy(x => side == Side.Buy ? x.Level!.Value.Price : -x.Level!.Value.Price)
                    .ThenByDescending(x => x.Level!.Value.Quantity)
                    .FirstOrDefault();
                if (best.Level is null)
                {
                    return executions;
                }

                tops.Add((best.Book, leg, side, best.Level.Value));
            }

            var strategyPrice = tops.Sum(t => (t.Leg.Side == Side.Buy ? 1 : -1) * t.Leg.Ratio * t.Level.Price);
            var marketable = spread.Side == Side.Buy ? strategyPrice <= spread.Limit : strategyPrice >= spread.Limit;
            var units = Math.Min(spread.Remaining, tops.Min(t => Math.Floor(t.Level.Quantity / t.Leg.Ratio)));
            if (!marketable || units <= 0)
            {
                return executions;
            }

            var legFills = new List<LegFill>();
            foreach (var (book, leg, side, level) in tops)
            {
                var qty = units * leg.Ratio;
                var result = book.Submit(_nextLegOrderId--, side, OrderType.Limit, TimeInForce.ImmediateOrCancel, level.Price, qty);
                AfterFills(book, side, result.Fills);
                legFills.Add(new LegFill(leg.ContractId, side, level.Price, result.FilledQuantity) { Exchange = book.Exchange });
            }

            spread.Remaining -= units;
            executions.Add(new SpreadExecution(units, strategyPrice, legFills));
        }

        return executions;
    }

    private sealed class RestingSpread(long orderId, IReadOnlyList<SpreadLeg> legs, Side side, decimal limit, decimal units)
    {
        public long OrderId { get; } = orderId;
        public IReadOnlyList<SpreadLeg> Legs { get; } = legs;
        public Side Side { get; } = side;
        public decimal Limit { get; } = limit;
        public decimal Remaining { get; set; } = units;
    }

    private void PublishDirty()
    {
        var now = Now;
        foreach (var id in _dirty)
        {
            var books = _venues.Select(v => v[id]).ToList();
            var g = _theo.GetValueOrDefault(id);
            var bids = Merge(books, Side.Buy);
            var asks = Merge(books, Side.Sell);
            var c = books[0].Contract;
            var t = YearsToExpiry(c.Expiry);
            var iv = _surface.Vol(_spot, (double)c.Strike, t, _options.RiskFreeRate, _spec.DividendYield);
            var tape = _tape.TryGetValue(id, out var last) ? last : ((decimal?)null, 0m);
            _cache.Publish(new QuoteSnapshot(id, g.Price, iv, g.Delta, g.Gamma, g.Vega / 100, g.Theta / 365,
                bids.Count > 0 ? bids[0].Price : null, bids.Count > 0 ? bids[0].Quantity : 0,
                asks.Count > 0 ? asks[0].Price : null, asks.Count > 0 ? asks[0].Quantity : 0, tape.Item1, tape.Item2, bids, asks, now)
            {
                Venues = [.. books.Select(b => new VenueQuote(b.Exchange, b.BestBid?.Price, b.BestBid?.Quantity ?? 0, b.BestAsk?.Price,
                    b.BestAsk?.Quantity ?? 0))],
            });
        }

        _dirty.Clear();
    }

    private void Emit(VenueEvent e) => _sink()?.OnVenueEvent(e);

    private int IndexOf(string exchange)
    {
        for (var i = 0; i < _exchanges.Count; i++)
        {
            if (_exchanges[i].Code.Equals(exchange, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Consolidated depth: every exchange's levels merged by price, best first, five levels.</summary>
    private static List<BookLevel> Merge(List<OrderBook> books, Side side)
    {
        var levels = books.SelectMany(b => b.Depth(side, 5))
            .GroupBy(l => l.Price)
            .Select(g => new BookLevel(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.Orders)));
        return [.. (side == Side.Buy ? levels.OrderByDescending(l => l.Price) : levels.OrderBy(l => l.Price)).Take(5)];
    }

    private double YearsToExpiry(DateOnly expiry) =>
        Math.Max((InstrumentRegistry.CloseOf(expiry) - Now).TotalDays / 365.0, 1.0 / (365 * 24));

    [LoggerMessage(Level = LogLevel.Error, Message = "Venue shard {Underlying} error")]
    private static partial void ShardError(ILogger logger, Exception ex, string underlying);
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

    public void Submit(long orderId, OptionContract contract, Side side, OrderType type, TimeInForce tif, decimal? price, decimal quantity,
        string? destination = null) =>
        ShardFor(contract).Submit(orderId, contract.Id, side, type, tif, price, quantity, destination);

    public void Cancel(long orderId, OptionContract contract) => ShardFor(contract).Cancel(orderId);

    public void SubmitSpread(long orderId, string underlying, IReadOnlyList<SpreadLeg> legs, Side side, TimeInForce tif, decimal limit,
        decimal units) => _shards[underlying].SubmitSpread(orderId, legs, side, tif, limit, units);

    public void Replace(long orderId, OptionContract contract, decimal price, decimal newTotalQuantity) =>
        ShardFor(contract).Replace(orderId, price, newTotalQuantity);

    public void List(IReadOnlyList<OptionContract> contracts)
    {
        foreach (var shard in _shards.Values)
        {
            shard.List(contracts);
        }
    }

    public void Delist(DateOnly expiry)
    {
        foreach (var shard in _shards.Values)
        {
            shard.Delist(expiry);
        }
    }

    public Task FlushAsync() => Task.WhenAll(_shards.Values.Select(s => s.FlushAsync()));

    private int _disposeState2;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState2, 1) != 0)
        {
            return;
        }

        foreach (var shard in _shards.Values)
        {
            await shard.DisposeAsync().ConfigureAwait(false);
        }
    }
}
