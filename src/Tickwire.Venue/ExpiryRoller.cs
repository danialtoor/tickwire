namespace Tickwire.Venue;

public sealed record RollResult(IReadOnlyList<DateOnly> Delisted, IReadOnlyList<DateOnly> Listed);

/// <summary>
/// Keeps a fixed number of weekly expiries listed. Expiries whose close has passed are delisted (resting orders
/// expire), and new Fridays are listed with strikes centered on the current underlying price.
/// </summary>
public sealed class ExpiryRoller(InstrumentRegistry instruments, SimulatedVenue venue, MarketDataCache marketData, int listedExpiries = 4)
{
    public RollResult Roll(DateTime utcNow)
    {
        var delisted = new List<DateOnly>();
        foreach (var expiry in instruments.Expiries.Where(e => InstrumentRegistry.CloseOf(e) <= utcNow))
        {
            venue.Delist(expiry); // venue first: it reports resting orders as expired while it still knows them
            instruments.Delist(expiry);
            delisted.Add(expiry);
        }

        var listed = new List<DateOnly>();
        var spots = marketData.Underlyings.ToDictionary(u => u.Symbol, u => u.Price, StringComparer.OrdinalIgnoreCase);
        while (instruments.Expiries.Count < listedExpiries)
        {
            var after = instruments.Expiries.Count > 0 ? instruments.Expiries[^1] : DateOnly.FromDateTime(utcNow);
            var next = InstrumentRegistry.NextFridays(after, 1)[0];
            var added = instruments.ListExpiry(next, spots);
            venue.List(added);
            listed.Add(next);
        }

        return new RollResult(delisted, listed);
    }
}
