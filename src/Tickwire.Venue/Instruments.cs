using System.Collections.Frozen;
using Tickwire.Pricing;

namespace Tickwire.Venue;

public enum Side
{
    Buy = 1,
    Sell = 2,
}

public enum OrderType
{
    Market = 1,
    Limit = 2,
}

public enum TimeInForce
{
    Day = 0,
    ImmediateOrCancel = 3,
    FillOrKill = 4,
}

/// <summary>A listed equity option. Prices are per share; one contract covers <see cref="Multiplier"/> shares.</summary>
public sealed record OptionContract(int Id, string Underlying, DateOnly Expiry, OptionRight Right, decimal Strike)
{
    public const int Multiplier = 100;

    public OccSymbol Occ => new(Underlying, Expiry, Right, Strike);

    public string OccSymbol { get; } = new OccSymbol(Underlying, Expiry, Right, Strike).ToString();

    /// <summary>Penny increments throughout (all four simulated underlyings are Penny Program names).</summary>
    public decimal TickSize => 0.01m;

    public string Display => $"{Underlying} {Expiry:dd MMM yy} {Strike:0.##} {(Right == OptionRight.Call ? "C" : "P")}";
}

public sealed record UnderlyingSpec(
    string Symbol,
    string Name,
    double InitialPrice,
    double RealizedVol,
    double AtmImpliedVol,
    double DividendYield,
    decimal StrikeStep,
    int StrikesEachSide);

/// <summary>All tradable contracts. Chains are generated around each underlying's price when an expiry is listed.</summary>
public sealed class InstrumentRegistry
{
    public static readonly UnderlyingSpec[] DefaultUnderlyings =
    [
        new("SPY", "SPDR S&P 500 ETF", 560, 0.14, 0.15, 0.013, 5m, 8),
        new("AAPL", "Apple Inc.", 230, 0.24, 0.26, 0.004, 5m, 8),
        new("TSLA", "Tesla Inc.", 250, 0.55, 0.58, 0, 10m, 8),
        new("NVDA", "NVIDIA Corp.", 130, 0.45, 0.48, 0.0003, 5m, 8),
    ];

    private readonly FrozenDictionary<string, UnderlyingSpec> _underlyings;
    private readonly Lock _writeLock = new();
    private volatile Snapshot _snapshot;
    private int _nextId;

    /// <summary>
    /// The listed contracts, rebuilt as a whole on every change so readers on any thread see a consistent set without
    /// locking. Expiries are added and removed by the daily roll (<see cref="ListExpiry"/>, <see cref="Delist"/>).
    /// </summary>
    private sealed record Snapshot(
        FrozenDictionary<int, OptionContract> ById,
        FrozenDictionary<string, OptionContract> ByOcc,
        FrozenDictionary<string, OptionContract[]> ByUnderlying)
    {
        public static Snapshot From(IEnumerable<OptionContract> contracts)
        {
            var list = contracts.ToList();
            return new Snapshot(
                list.ToFrozenDictionary(c => c.Id),
                list.ToFrozenDictionary(c => c.OccSymbol.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparer.OrdinalIgnoreCase),
                list.GroupBy(c => c.Underlying)
                    .ToFrozenDictionary(g => g.Key, g => g.OrderBy(c => c.Expiry).ThenBy(c => c.Strike).ThenBy(c => c.Right).ToArray(),
                        StringComparer.OrdinalIgnoreCase));
        }
    }

    public InstrumentRegistry(IEnumerable<UnderlyingSpec> underlyings, IEnumerable<OptionContract> contracts)
    {
        var list = contracts.ToList();
        _snapshot = Snapshot.From(list);
        _nextId = list.Count == 0 ? 0 : list.Max(c => c.Id);
        _underlyings = underlyings.ToFrozenDictionary(u => u.Symbol, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<UnderlyingSpec> Underlyings => _underlyings.Values;
    public IReadOnlyCollection<OptionContract> All => _snapshot.ById.Values;

    /// <summary>Distinct listed expiries, earliest first.</summary>
    public IReadOnlyList<DateOnly> Expiries => [.. _snapshot.ById.Values.Select(c => c.Expiry).Distinct().Order()];

    public static InstrumentRegistry Build(DateOnly today, int expiries = 4, IEnumerable<UnderlyingSpec>? underlyings = null)
    {
        var specs = (underlyings ?? DefaultUnderlyings).ToList();
        var registry = new InstrumentRegistry(specs, []);
        foreach (var expiry in NextFridays(today, expiries))
        {
            registry.ListExpiry(expiry, specs.ToDictionary(u => u.Symbol, u => u.InitialPrice));
        }

        return registry;
    }

    /// <summary>
    /// Lists one expiry for every underlying, with strikes centered on the given spot prices (the starting price when a
    /// spot is missing). Returns the new contracts; an expiry that's already listed is left alone.
    /// </summary>
    public IReadOnlyList<OptionContract> ListExpiry(DateOnly expiry, IReadOnlyDictionary<string, double> spots)
    {
        lock (_writeLock)
        {
            var current = _snapshot;
            if (current.ById.Values.Any(c => c.Expiry == expiry))
            {
                return [];
            }

            var added = new List<OptionContract>();
            foreach (var u in _underlyings.Values)
            {
                var spot = spots.TryGetValue(u.Symbol, out var s) && s > 0 ? s : u.InitialPrice;
                var atm = Math.Round((decimal)spot / u.StrikeStep) * u.StrikeStep;
                for (var i = -u.StrikesEachSide; i <= u.StrikesEachSide; i++)
                {
                    var strike = atm + (i * u.StrikeStep);
                    if (strike <= 0)
                    {
                        continue;
                    }

                    added.Add(new OptionContract(++_nextId, u.Symbol, expiry, OptionRight.Call, strike));
                    added.Add(new OptionContract(++_nextId, u.Symbol, expiry, OptionRight.Put, strike));
                }
            }

            _snapshot = Snapshot.From(current.ById.Values.Concat(added));
            return added;
        }
    }

    /// <summary>Removes every contract of an expiry. Returns what was removed.</summary>
    public IReadOnlyList<OptionContract> Delist(DateOnly expiry)
    {
        lock (_writeLock)
        {
            var current = _snapshot;
            var removed = current.ById.Values.Where(c => c.Expiry == expiry).ToList();
            if (removed.Count > 0)
            {
                _snapshot = Snapshot.From(current.ById.Values.Where(c => c.Expiry != expiry));
            }

            return removed;
        }
    }

    /// <summary>Options stop trading at 16:00 New York time on expiry day; 20:00 UTC is used throughout the simulation.</summary>
    public static DateTime CloseOf(DateOnly expiry) => expiry.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);

    public static IReadOnlyList<DateOnly> NextFridays(DateOnly today, int count)
    {
        var result = new List<DateOnly>(count);
        var d = today.AddDays(1);
        while (result.Count < count)
        {
            if (d.DayOfWeek == DayOfWeek.Friday)
            {
                result.Add(d);
            }

            d = d.AddDays(1);
        }

        return result;
    }

    public UnderlyingSpec? Underlying(string symbol) => _underlyings.GetValueOrDefault(symbol);

    public OptionContract? Get(int id) => _snapshot.ById.GetValueOrDefault(id);

    public IReadOnlyList<OptionContract> Chain(string underlying) =>
        _snapshot.ByUnderlying.TryGetValue(underlying, out var chain) ? chain : [];

    public OptionContract? Find(string occSymbol) =>
        _snapshot.ByOcc.GetValueOrDefault(occSymbol.Replace(" ", string.Empty, StringComparison.Ordinal));

    public OptionContract? Find(string underlying, DateOnly expiry, OptionRight right, decimal strike) =>
        Chain(underlying).FirstOrDefault(c => c.Expiry == expiry && c.Right == right && c.Strike == strike);
}
