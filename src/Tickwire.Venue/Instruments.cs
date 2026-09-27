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

/// <summary>All tradable contracts. Chains are generated around each underlying's starting price.</summary>
public sealed class InstrumentRegistry
{
    public static readonly UnderlyingSpec[] DefaultUnderlyings =
    [
        new("SPY", "SPDR S&P 500 ETF", 560, 0.14, 0.15, 0.013, 5m, 8),
        new("AAPL", "Apple Inc.", 230, 0.24, 0.26, 0.004, 5m, 8),
        new("TSLA", "Tesla Inc.", 250, 0.55, 0.58, 0, 10m, 8),
        new("NVDA", "NVIDIA Corp.", 130, 0.45, 0.48, 0.0003, 5m, 8),
    ];

    private readonly FrozenDictionary<int, OptionContract> _byId;
    private readonly FrozenDictionary<string, OptionContract> _byOcc;
    private readonly FrozenDictionary<string, OptionContract[]> _byUnderlying;
    private readonly FrozenDictionary<string, UnderlyingSpec> _underlyings;

    public InstrumentRegistry(IEnumerable<UnderlyingSpec> underlyings, IEnumerable<OptionContract> contracts)
    {
        var list = contracts.ToList();
        _byId = list.ToFrozenDictionary(c => c.Id);
        _byOcc = list.ToFrozenDictionary(c => c.OccSymbol.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparer.OrdinalIgnoreCase);
        _byUnderlying = list.GroupBy(c => c.Underlying)
            .ToFrozenDictionary(g => g.Key, g => g.OrderBy(c => c.Expiry).ThenBy(c => c.Strike).ThenBy(c => c.Right).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        _underlyings = underlyings.ToFrozenDictionary(u => u.Symbol, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<UnderlyingSpec> Underlyings => _underlyings.Values;
    public IReadOnlyCollection<OptionContract> All => _byId.Values;

    public static InstrumentRegistry Build(DateOnly today, int expiries = 4, IEnumerable<UnderlyingSpec>? underlyings = null)
    {
        var specs = (underlyings ?? DefaultUnderlyings).ToList();
        var contracts = new List<OptionContract>();
        var id = 1;
        var dates = NextFridays(today, expiries);
        foreach (var u in specs)
        {
            var atm = Math.Round((decimal)u.InitialPrice / u.StrikeStep) * u.StrikeStep;
            foreach (var expiry in dates)
            {
                for (var i = -u.StrikesEachSide; i <= u.StrikesEachSide; i++)
                {
                    var strike = atm + (i * u.StrikeStep);
                    contracts.Add(new OptionContract(id++, u.Symbol, expiry, OptionRight.Call, strike));
                    contracts.Add(new OptionContract(id++, u.Symbol, expiry, OptionRight.Put, strike));
                }
            }
        }

        return new InstrumentRegistry(specs, contracts);
    }

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

    public OptionContract? Get(int id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<OptionContract> Chain(string underlying) =>
        _byUnderlying.TryGetValue(underlying, out var chain) ? chain : [];

    public OptionContract? Find(string occSymbol) =>
        _byOcc.GetValueOrDefault(occSymbol.Replace(" ", string.Empty, StringComparison.Ordinal));

    public OptionContract? Find(string underlying, DateOnly expiry, OptionRight right, decimal strike) =>
        Chain(underlying).FirstOrDefault(c => c.Expiry == expiry && c.Right == right && c.Strike == strike);
}
