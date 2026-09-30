namespace Tickwire.Venue;

/// <summary>Whether a fill added liquidity (the order was resting) or removed it (the order crossed the spread).</summary>
public enum Liquidity
{
    Added = 1,
    Removed = 2,
}

/// <summary>
/// One simulated options exchange. Fees are per contract, in dollars; a negative maker fee is a rebate. The market
/// makers listed quote only on this exchange, so each one has its own spread and depth. Names are made up.
/// </summary>
public sealed record Exchange(string Code, string Name, decimal TakerFee, decimal MakerFee, IReadOnlyList<MakerProfile> Makers)
{
    public decimal Fee(Liquidity liquidity) => liquidity == Liquidity.Removed ? TakerFee : MakerFee;
}

public sealed record MakerProfile(string Name, double HalfSpreadPct, decimal MinHalfSpread, int MinSize, int MaxSize);

public static class Exchanges
{
    /// <summary>The primary exchange: spreads trade here, and it's where the original two makers quote.</summary>
    public static readonly Exchange Primary = new("TWX", "Tickwire Options", TakerFee: 0.50m, MakerFee: -0.20m,
    [
        new("MM-ALPHA", HalfSpreadPct: 0.03, MinHalfSpread: 0.02m, MinSize: 5, MaxSize: 20),
        new("MM-BETA", HalfSpreadPct: 0.06, MinHalfSpread: 0.05m, MinSize: 20, MaxSize: 60),
    ]);

    /// <summary>Cheap to take from, costs to post: wider quotes with more size.</summary>
    public static readonly Exchange Nova = new("NOVA", "Nova Options", TakerFee: 0.15m, MakerFee: 0.10m,
    [
        new("MM-GAMMA", HalfSpreadPct: 0.045, MinHalfSpread: 0.03m, MinSize: 10, MaxSize: 40),
    ]);

    /// <summary>Big maker rebate, expensive to take from: the tightest quotes, but small.</summary>
    public static readonly Exchange Argo = new("ARGO", "Argo Options", TakerFee: 0.65m, MakerFee: -0.40m,
    [
        new("MM-DELTA", HalfSpreadPct: 0.02, MinHalfSpread: 0.01m, MinSize: 2, MaxSize: 10),
    ]);

    public static readonly IReadOnlyList<Exchange> All = [Primary, Nova, Argo];

    public static Exchange? Find(string? code) =>
        code is null ? null : All.FirstOrDefault(e => e.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An exchange's top of book for one contract, as the router sees it.</summary>
public readonly record struct VenueTop(Exchange Exchange, BookLevel? Bid, BookLevel? Ask);

public sealed record RouteDecision(Exchange Exchange, string Reason);

/// <summary>
/// The smart order router. Each order goes whole to one exchange (no splitting):
/// <list type="bullet">
/// <item>Marketable orders go where the all-in price is best: the displayed price plus the taker fee when buying (minus it
/// when selling), per share. Ties go to the bigger displayed size, then the cheaper fee.</item>
/// <item>Orders that would rest go where posting pays best (the lowest maker fee, i.e. the biggest rebate).</item>
/// </list>
/// A directed order (ExDestination) skips all of this.
/// </summary>
public static class OrderRouter
{
    public static RouteDecision Route(IReadOnlyList<VenueTop> tops, Side side, OrderType type, decimal? limit, int multiplier = 100)
    {
        var candidates = new List<(VenueTop Top, BookLevel Level, decimal AllIn)>();
        foreach (var top in tops)
        {
            var level = side == Side.Buy ? top.Ask : top.Bid;
            if (level is not { Quantity: > 0 } l)
            {
                continue;
            }

            var marketable = type == OrderType.Market || (side == Side.Buy ? l.Price <= limit : l.Price >= limit);
            if (!marketable)
            {
                continue;
            }

            var feePerShare = top.Exchange.TakerFee / multiplier;
            candidates.Add((top, l, side == Side.Buy ? l.Price + feePerShare : l.Price - feePerShare));
        }

        if (candidates.Count > 0)
        {
            var best = (side == Side.Buy ? candidates.OrderBy(c => c.AllIn) : candidates.OrderByDescending(c => c.AllIn))
                .ThenByDescending(c => c.Level.Quantity)
                .ThenBy(c => c.Top.Exchange.TakerFee)
                .First();
            var others = candidates.Count > 1
                ? $"; next best {string.Join(", ", candidates.Where(c => c != best).Select(c => $"{c.Top.Exchange.Code} {c.AllIn:0.####}"))}"
                : string.Empty;
            return new RouteDecision(best.Top.Exchange,
                $"Routed to {best.Top.Exchange.Code}: best all-in price {best.AllIn:0.####} ({best.Level.Price} {(side == Side.Buy ? "+" : "-")} ${best.Top.Exchange.TakerFee:0.00} fee){others}");
        }

        var passive = tops.Select(t => t.Exchange).OrderBy(e => e.MakerFee).First();
        return new RouteDecision(passive,
            $"Routed to {passive.Code}: not marketable anywhere, so it rests where posting pays best (maker fee {passive.MakerFee:0.00})");
    }
}
