using System.Globalization;

namespace Tickwire.Pricing;

/// <summary>
/// OCC 21-character option symbol: root (6, space padded), expiry yymmdd, C/P, strike × 1000 (8 digits).
/// Example: "SPY   250620C00550000" = SPY 20 Jun 2025 550 call.
/// </summary>
public readonly record struct OccSymbol(string Root, DateOnly Expiry, OptionRight Right, decimal Strike)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Root,-6}{Expiry:yyMMdd}{(Right == OptionRight.Call ? 'C' : 'P')}{(long)(Strike * 1000):00000000}");

    public static bool TryParse(string? text, out OccSymbol symbol)
    {
        symbol = default;
        if (text is null)
        {
            return false;
        }

        // Accept the padded 21-char form and the compact form without padding ("SPY250620C00550000").
        var s = text.Trim();
        if (s.Length < 16)
        {
            return false;
        }

        var tail = s[^15..];
        var root = s[..^15].Trim();
        if (root.Length is 0 or > 6
            || !DateOnly.TryParseExact(tail[..6], "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)
            || tail[6] is not ('C' or 'P')
            || !long.TryParse(tail[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var strikeMilli))
        {
            return false;
        }

        symbol = new OccSymbol(root.ToUpperInvariant(), expiry, tail[6] == 'C' ? OptionRight.Call : OptionRight.Put,
            strikeMilli / 1000m);
        return true;
    }
}

/// <summary>
/// A simple volatility surface: ATM vol per underlying with linear skew and quadratic smile in
/// standardized moneyness m = ln(K/F) / sqrt(T). Good enough to make the chain look like a real one.
/// </summary>
public sealed record VolSurface(double AtmVol, double Skew = -0.10, double Smile = 0.03, double MinVol = 0.05, double MaxVol = 3.0)
{
    public double Vol(double spot, double strike, double t, double r = 0, double q = 0)
    {
        var tt = Math.Max(t, 1.0 / 365);
        var forward = spot * Math.Exp((r - q) * tt);
        var m = Math.Log(strike / forward) / Math.Sqrt(tt);
        var vol = AtmVol * (1 + (Skew * m) + (Smile * m * m));
        return Math.Clamp(vol, MinVol, MaxVol);
    }
}

/// <summary>Geometric Brownian motion step: S' = S · exp((μ − σ²/2)dt + σ√dt · Z).</summary>
public static class Gbm
{
    public static double Step(double spot, double drift, double vol, double dtYears, double z) =>
        spot * Math.Exp(((drift - (0.5 * vol * vol)) * dtYears) + (vol * Math.Sqrt(dtYears) * z));

    /// <summary>
    /// Mean-reverting step on log price (Ornstein-Uhlenbeck): ln S' = ln S + κ(ln anchor − ln S)dt − σ²/2·dt + σ√dt·Z.
    /// With κ = 12/yr the price wanders about σ/√(2κ) around the anchor (≈3% for SPY) instead of drifting away for ever.
    /// </summary>
    public static double MeanRevertingStep(double spot, double anchor, double kappa, double vol, double dtYears, double z)
    {
        var x = Math.Log(spot);
        var pull = kappa * (Math.Log(anchor) - x) * dtYears;
        return Math.Exp(x + pull - (0.5 * vol * vol * dtYears) + (vol * Math.Sqrt(dtYears) * z));
    }

    /// <summary>Standard normal sample via Box-Muller.</summary>
    public static double NextGaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
