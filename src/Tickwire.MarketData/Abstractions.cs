using System.Globalization;
using Tickwire.Pricing;

namespace Tickwire.MarketData;

/// <summary>A top-of-book quote for one option contract from an external feed.</summary>
public sealed record ExternalQuote(
    string OccSymbol,
    decimal? Bid,
    decimal? Ask,
    decimal? BidSize,
    decimal? AskSize,
    decimal? Last,
    double? ImpliedVol,
    double? Delta,
    DateTime Time,
    string Provider);

/// <summary>An underlying price from an external feed.</summary>
public sealed record ExternalUnderlying(string Symbol, decimal Price, DateTime Time, string Provider);

/// <summary>One input a provider needs (API key, secret, dataset, ...).</summary>
public sealed record CredentialField(string Name, string Label, bool Secret, string? Placeholder = null, string? DefaultValue = null);

/// <summary>What the UI needs to render a provider card.</summary>
public sealed record ProviderInfo(
    string Id,
    string Name,
    string Tagline,
    string Transport,
    string DocsUrl,
    string SignupUrl,
    IReadOnlyList<CredentialField> Credentials,
    IReadOnlyList<string> Provides,
    string BrandColor,
    bool Verified);

/// <summary>Which contracts to stream. Adapters subscribe as narrowly as their API allows.</summary>
public sealed record FeedSubscription(IReadOnlyList<string> Underlyings, IReadOnlyList<OccSymbol> Contracts);

/// <summary>Receives normalized data. Called from the adapter's read loop; implementations must be fast and thread-safe.</summary>
public interface IFeedSink
{
    void OnQuote(ExternalQuote quote);

    void OnUnderlying(ExternalUnderlying underlying);

    void OnStatus(string message);
}

/// <summary>
/// An options market data provider. <see cref="RunAsync"/> connects, authenticates, subscribes and streams until the
/// token is canceled or the connection fails (it throws on failure; the caller reports the error to the user).
/// Credentials are passed in per call and never stored by the adapter.
/// </summary>
public interface IOptionFeedProvider
{
    ProviderInfo Info { get; }

    Task RunAsync(IReadOnlyDictionary<string, string> credentials, FeedSubscription subscription, IFeedSink sink,
        CancellationToken cancellationToken);
}

public sealed class FeedAuthException(string message) : Exception(message);

/// <summary>Contract symbol formats used by the different vendors.</summary>
public static class Symbology
{
    /// <summary>"SPY261002C00560000" (OCC without root padding): Tradier, Alpaca.</summary>
    public static string Compact(OccSymbol s) => s.ToString().Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>"O:SPY261002C00560000": Polygon.io.</summary>
    public static string Polygon(OccSymbol s) => "O:" + Compact(s);

    /// <summary>"SPY   261002C00560000" (21-char OCC, root padded to 6): OPRA raw symbols, Databento.</summary>
    public static string Occ21(OccSymbol s) => s.ToString();

    /// <summary>Parses any of the formats above back to an OCC symbol.</summary>
    public static bool TryParse(string? vendorSymbol, out OccSymbol symbol)
    {
        symbol = default;
        if (string.IsNullOrWhiteSpace(vendorSymbol))
        {
            return false;
        }

        var s = vendorSymbol.StartsWith("O:", StringComparison.Ordinal) ? vendorSymbol[2..] : vendorSymbol;
        return OccSymbol.TryParse(s, out symbol);
    }

    public static decimal? ParseDecimal(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}

internal static class Required
{
    public static string Get(IReadOnlyDictionary<string, string> credentials, string name) =>
        credentials.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.Trim()
            : throw new FeedAuthException($"Missing {name}");
}
