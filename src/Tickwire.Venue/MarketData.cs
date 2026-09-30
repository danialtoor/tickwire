using System.Collections.Concurrent;

namespace Tickwire.Venue;

public sealed record QuoteSnapshot(
    int ContractId,
    double Theo,
    double ImpliedVol,
    double Delta,
    double Gamma,
    double Vega,
    double Theta,
    decimal? Bid,
    decimal BidSize,
    decimal? Ask,
    decimal AskSize,
    decimal? Last,
    decimal Volume,
    IReadOnlyList<BookLevel> Bids,
    IReadOnlyList<BookLevel> Asks,
    DateTime Time)
{
    /// <summary>Each exchange's own top of book. Bid/Ask above are the consolidated best (NBBO), Bids/Asks the merged depth.</summary>
    public IReadOnlyList<VenueQuote> Venues { get; init; } = [];
}

public sealed record VenueQuote(string Exchange, decimal? Bid, decimal BidSize, decimal? Ask, decimal AskSize);

public sealed record UnderlyingSnapshot(string Symbol, double Price, double Open, DateTime Time)
{
    public double ChangePct => Open == 0 ? 0 : (Price - Open) / Open * 100;
}

public sealed record TradePrint(int ContractId, string Underlying, decimal Price, decimal Quantity, Side AggressorSide, DateTime Time)
{
    public string Exchange { get; init; } = "TWX";
}

/// <summary>
/// Latest market state, written by venue shards and read by the OMS (price bands), REST and the SignalR publisher.
/// Snapshots are immutable records, so readers never see a half-updated quote.
/// </summary>
public sealed class MarketDataCache
{
    private const int MaxPrints = 200;
    private readonly ConcurrentDictionary<int, QuoteSnapshot> _quotes = new();
    private readonly ConcurrentDictionary<string, UnderlyingSnapshot> _underlyings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<TradePrint> _prints = new();
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public QuoteSnapshot? Quote(int contractId) => _quotes.GetValueOrDefault(contractId);

    public UnderlyingSnapshot? Underlying(string symbol) => _underlyings.GetValueOrDefault(symbol);

    public IEnumerable<UnderlyingSnapshot> Underlyings => _underlyings.Values;

    public IReadOnlyList<TradePrint> RecentPrints(string? underlying = null, int max = 50) =>
        [.. _prints.Reverse().Where(p => underlying is null || p.Underlying.Equals(underlying, StringComparison.OrdinalIgnoreCase)).Take(max)];

    public void Remove(int contractId)
    {
        _quotes.TryRemove(contractId, out _);
        Interlocked.Increment(ref _version);
    }

    public void Publish(QuoteSnapshot quote)
    {
        _quotes[quote.ContractId] = quote;
        Interlocked.Increment(ref _version);
    }

    public void Publish(UnderlyingSnapshot underlying)
    {
        _underlyings[underlying.Symbol] = underlying;
        Interlocked.Increment(ref _version);
    }

    public void Publish(TradePrint print)
    {
        _prints.Enqueue(print);
        while (_prints.Count > MaxPrints && _prints.TryDequeue(out _))
        {
        }
    }
}
