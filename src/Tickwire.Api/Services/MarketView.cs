using Tickwire.Pricing;
using Tickwire.Venue;

namespace Tickwire.Api.Services;

public sealed record QuoteDto(
    int Id,
    string Occ,
    double Theo,
    double Iv,
    double Delta,
    double Gamma,
    double Vega,
    double Theta,
    decimal? Bid,
    decimal BidSize,
    decimal? Ask,
    decimal AskSize,
    decimal? Last,
    decimal Volume);

public sealed record ChainRowDto(decimal Strike, QuoteDto? Call, QuoteDto? Put);

public sealed record ChainDto(string Underlying, DateOnly Expiry, double Spot, double ChangePct, IReadOnlyList<ChainRowDto> Rows, DateTime Time);

public sealed record UnderlyingDto(string Symbol, string Name, double Price, double ChangePct, IReadOnlyList<DateOnly> Expiries);

public sealed record BookDto(int ContractId, string Occ, string Display, string Underlying, DateOnly Expiry, decimal Strike, string Right,
    double Theo, double Iv, decimal? Last, decimal Volume, IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks, DateTime Time);

public sealed record TradePrintDto(int ContractId, string Display, decimal Price, decimal Quantity, string Side, DateTime Time);

/// <summary>Read-side projections of market data for REST and the live hub.</summary>
public sealed class MarketView(InstrumentRegistry instruments, MarketDataCache cache, TimeProvider time)
{
    public IReadOnlyList<UnderlyingDto> Underlyings() =>
    [
        .. instruments.Underlyings.Select(u =>
        {
            var snap = cache.Underlying(u.Symbol);
            return new UnderlyingDto(u.Symbol, u.Name, Math.Round(snap?.Price ?? u.InitialPrice, 2), Math.Round(snap?.ChangePct ?? 0, 3),
                [.. instruments.Chain(u.Symbol).Select(c => c.Expiry).Distinct().Order()]);
        }),
    ];

    public ChainDto? Chain(string underlying, DateOnly? expiry)
    {
        var chain = instruments.Chain(underlying);
        if (chain.Count == 0)
        {
            return null;
        }

        var exp = expiry ?? chain[0].Expiry;
        var rows = chain.Where(c => c.Expiry == exp)
            .GroupBy(c => c.Strike)
            .OrderBy(g => g.Key)
            .Select(g => new ChainRowDto(g.Key,
                Quote(g.FirstOrDefault(c => c.Right == OptionRight.Call)),
                Quote(g.FirstOrDefault(c => c.Right == OptionRight.Put))))
            .ToList();
        var snap = cache.Underlying(underlying);
        return new ChainDto(chain[0].Underlying, exp, Math.Round(snap?.Price ?? 0, 2), Math.Round(snap?.ChangePct ?? 0, 3), rows,
            time.GetUtcNow().UtcDateTime);
    }

    public BookDto? Book(int contractId)
    {
        var c = instruments.Get(contractId);
        if (c is null)
        {
            return null;
        }

        var q = cache.Quote(contractId);
        return new BookDto(c.Id, c.OccSymbol, c.Display, c.Underlying, c.Expiry, c.Strike, c.Right == OptionRight.Call ? "C" : "P",
            Math.Round(q?.Theo ?? 0, 4), Math.Round(q?.ImpliedVol ?? 0, 4), q?.Last, q?.Volume ?? 0, q?.Bids ?? [], q?.Asks ?? [],
            q?.Time ?? time.GetUtcNow().UtcDateTime);
    }

    public IReadOnlyList<TradePrintDto> Tape(string? underlying, int max = 30) =>
    [
        .. cache.RecentPrints(underlying, max).Select(p => new TradePrintDto(p.ContractId,
            instruments.Get(p.ContractId)?.Display ?? p.ContractId.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Price,
            p.Quantity, p.AggressorSide == Side.Buy ? "buy" : "sell", p.Time)),
    ];

    private QuoteDto? Quote(OptionContract? c)
    {
        if (c is null)
        {
            return null;
        }

        var q = cache.Quote(c.Id);
        return q is null
            ? new QuoteDto(c.Id, c.OccSymbol, 0, 0, 0, 0, 0, 0, null, 0, null, 0, null, 0)
            : new QuoteDto(c.Id, c.OccSymbol, Math.Round(q.Theo, 4), Math.Round(q.ImpliedVol, 4), Math.Round(q.Delta, 4),
                Math.Round(q.Gamma, 5), Math.Round(q.Vega, 4), Math.Round(q.Theta, 4), q.Bid, q.BidSize, q.Ask, q.AskSize, q.Last, q.Volume);
    }
}
