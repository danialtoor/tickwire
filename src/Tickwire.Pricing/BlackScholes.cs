namespace Tickwire.Pricing;

public enum OptionRight
{
    Put = 0,
    Call = 1,
}

public readonly record struct Greeks(double Price, double Delta, double Gamma, double Vega, double Theta, double Rho);

/// <summary>
/// Black-Scholes-Merton for European options with a continuous dividend yield.
/// Inputs: spot S, strike K, years to expiry T, risk-free rate r, dividend yield q, volatility sigma.
/// Vega and Rho are per 1.00 change (divide by 100 for "per point"); Theta is per year (divide by 365 for per day).
/// </summary>
public static class BlackScholes
{
    public static double Price(OptionRight right, double s, double k, double t, double r, double q, double sigma)
    {
        if (t <= 0 || sigma <= 0)
        {
            return IntrinsicForward(right, s, k, t, r, q);
        }

        var (d1, d2) = D1D2(s, k, t, r, q, sigma);
        var dfR = Math.Exp(-r * t);
        var dfQ = Math.Exp(-q * t);
        return right == OptionRight.Call
            ? (s * dfQ * Normal.Cdf(d1)) - (k * dfR * Normal.Cdf(d2))
            : (k * dfR * Normal.Cdf(-d2)) - (s * dfQ * Normal.Cdf(-d1));
    }

    public static Greeks Compute(OptionRight right, double s, double k, double t, double r, double q, double sigma)
    {
        if (t <= 0 || sigma <= 0)
        {
            var intrinsic = IntrinsicForward(right, s, k, t, r, q);
            var itm = right == OptionRight.Call ? s > k : s < k;
            return new Greeks(intrinsic, itm ? (right == OptionRight.Call ? 1 : -1) : 0, 0, 0, 0, 0);
        }

        var sqrtT = Math.Sqrt(t);
        var (d1, d2) = D1D2(s, k, t, r, q, sigma);
        var dfR = Math.Exp(-r * t);
        var dfQ = Math.Exp(-q * t);
        var pdf = Normal.Pdf(d1);
        var gamma = dfQ * pdf / (s * sigma * sqrtT);
        var vega = s * dfQ * pdf * sqrtT;
        var decay = -(s * dfQ * pdf * sigma) / (2 * sqrtT);

        if (right == OptionRight.Call)
        {
            var nd1 = Normal.Cdf(d1);
            var nd2 = Normal.Cdf(d2);
            return new Greeks(
                (s * dfQ * nd1) - (k * dfR * nd2),
                dfQ * nd1,
                gamma,
                vega,
                decay - (r * k * dfR * nd2) + (q * s * dfQ * nd1),
                k * t * dfR * nd2);
        }
        else
        {
            var nmd1 = Normal.Cdf(-d1);
            var nmd2 = Normal.Cdf(-d2);
            return new Greeks(
                (k * dfR * nmd2) - (s * dfQ * nmd1),
                -dfQ * nmd1,
                gamma,
                vega,
                decay + (r * k * dfR * nmd2) - (q * s * dfQ * nmd1),
                -k * t * dfR * nmd2);
        }
    }

    /// <summary>
    /// Implied volatility by Newton-Raphson on vega, falling back to bisection when Newton leaves the bracket or vega is
    /// tiny (deep in/out of the money). Returns null when the price is outside the no-arbitrage bounds.
    /// </summary>
    public static double? ImpliedVol(OptionRight right, double price, double s, double k, double t, double r, double q,
        double tolerance = 1e-8, int maxIterations = 100)
    {
        if (t <= 0 || price <= 0)
        {
            return null;
        }

        var dfR = Math.Exp(-r * t);
        var dfQ = Math.Exp(-q * t);
        var lower = right == OptionRight.Call ? Math.Max(0, (s * dfQ) - (k * dfR)) : Math.Max(0, (k * dfR) - (s * dfQ));
        var upper = right == OptionRight.Call ? s * dfQ : k * dfR;
        if (price < lower - tolerance || price > upper + tolerance)
        {
            return null;
        }

        double lo = 1e-6, hi = 10.0;
        // Brenner-Subrahmanyam starting point, clamped into the bracket.
        var sigma = Math.Clamp(Math.Sqrt(2 * Math.PI / t) * price / s, 0.05, 3.0);

        for (var i = 0; i < maxIterations; i++)
        {
            var g = Compute(right, s, k, t, r, q, sigma);
            var diff = g.Price - price;
            if (Math.Abs(diff) < tolerance)
            {
                return sigma;
            }

            if (diff > 0)
            {
                hi = sigma;
            }
            else
            {
                lo = sigma;
            }

            var next = g.Vega > 1e-10 ? sigma - (diff / g.Vega) : double.NaN;
            sigma = double.IsFinite(next) && next > lo && next < hi ? next : (lo + hi) / 2;
            if (hi - lo < tolerance * 1e-3)
            {
                return sigma;
            }
        }

        return sigma;
    }

    private static (double D1, double D2) D1D2(double s, double k, double t, double r, double q, double sigma)
    {
        var sqrtT = Math.Sqrt(t);
        var d1 = (Math.Log(s / k) + ((r - q + (0.5 * sigma * sigma)) * t)) / (sigma * sqrtT);
        return (d1, d1 - (sigma * sqrtT));
    }

    private static double IntrinsicForward(OptionRight right, double s, double k, double t, double r, double q)
    {
        var fwd = (s * Math.Exp(-q * Math.Max(t, 0))) - (k * Math.Exp(-r * Math.Max(t, 0)));
        return right == OptionRight.Call ? Math.Max(0, fwd) : Math.Max(0, -fwd);
    }
}

/// <summary>Standard normal distribution.</summary>
public static class Normal
{
    private const double InvSqrt2Pi = 0.39894228040143267794;

    public static double Pdf(double x) => InvSqrt2Pi * Math.Exp(-0.5 * x * x);

    /// <summary>
    /// Cumulative normal to double precision (~1e-15), Hart (1968) as given by West, "Better approximations to cumulative
    /// normal functions" (2005).
    /// </summary>
    public static double Cdf(double x)
    {
        var xabs = Math.Abs(x);
        double c;
        if (xabs > 37)
        {
            c = 0;
        }
        else
        {
            var e = Math.Exp(-xabs * xabs / 2);
            if (xabs < 7.07106781186547)
            {
                var b = (3.52624965998911E-02 * xabs) + 0.700383064443688;
                b = (b * xabs) + 6.37396220353165;
                b = (b * xabs) + 33.912866078383;
                b = (b * xabs) + 112.079291497871;
                b = (b * xabs) + 221.213596169931;
                b = (b * xabs) + 220.206867912376;
                c = e * b;
                b = (8.83883476483184E-02 * xabs) + 1.75566716318264;
                b = (b * xabs) + 16.064177579207;
                b = (b * xabs) + 86.7807322029461;
                b = (b * xabs) + 296.564248779674;
                b = (b * xabs) + 637.333633378831;
                b = (b * xabs) + 793.826512519948;
                b = (b * xabs) + 440.413735824752;
                c /= b;
            }
            else
            {
                var b = xabs + 0.65;
                b = xabs + (4 / b);
                b = xabs + (3 / b);
                b = xabs + (2 / b);
                b = xabs + (1 / b);
                c = e / b / 2.506628274631;
            }
        }

        return x > 0 ? 1 - c : c;
    }
}
