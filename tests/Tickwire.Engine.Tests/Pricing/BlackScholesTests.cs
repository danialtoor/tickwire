using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Tickwire.Pricing;

namespace Tickwire.Engine.Tests.Pricing;

public class BlackScholesTests
{
    [Fact]
    public void Matches_Hull_textbook_example()
    {
        // Hull, Options Futures and Other Derivatives, Example 15.6: S=42, K=40, r=10%, sigma=20%, T=0.5.
        BlackScholes.Price(OptionRight.Call, 42, 40, 0.5, 0.10, 0, 0.20).Should().BeApproximately(4.7594, 1e-4);
        BlackScholes.Price(OptionRight.Put, 42, 40, 0.5, 0.10, 0, 0.20).Should().BeApproximately(0.8086, 1e-4);
    }

    [Fact]
    public void Matches_reference_greeks()
    {
        // Reference values for S=100, K=100, T=1, r=5%, q=0, sigma=20% (standard textbook case).
        var call = BlackScholes.Compute(OptionRight.Call, 100, 100, 1, 0.05, 0, 0.20);
        call.Price.Should().BeApproximately(10.4506, 1e-4);
        call.Delta.Should().BeApproximately(0.6368, 1e-4);
        call.Gamma.Should().BeApproximately(0.018762, 1e-5);
        call.Vega.Should().BeApproximately(37.5240, 1e-3);
        call.Theta.Should().BeApproximately(-6.4140, 1e-3);
        call.Rho.Should().BeApproximately(53.2325, 1e-3);

        var put = BlackScholes.Compute(OptionRight.Put, 100, 100, 1, 0.05, 0, 0.20);
        put.Price.Should().BeApproximately(5.5735, 1e-4);
        put.Delta.Should().BeApproximately(-0.3632, 1e-4);
    }

    [Fact]
    public void Normal_cdf_is_accurate()
    {
        Normal.Cdf(0).Should().BeApproximately(0.5, 1e-15);
        Normal.Cdf(1.96).Should().BeApproximately(0.9750021048517795, 1e-12);
        Normal.Cdf(-3).Should().BeApproximately(0.0013498980316301, 1e-13);
        Normal.Cdf(10).Should().BeApproximately(1, 1e-15);
    }

    [Fact]
    public void Implied_vol_handles_deep_out_of_the_money_with_bisection_fallback()
    {
        var price = BlackScholes.Price(OptionRight.Call, 100, 180, 0.1, 0.05, 0, 0.9);

        BlackScholes.ImpliedVol(OptionRight.Call, price, 100, 180, 0.1, 0.05, 0)!.Value.Should().BeApproximately(0.9, 1e-5);
    }

    [Fact]
    public void Implied_vol_rejects_prices_outside_arbitrage_bounds()
    {
        BlackScholes.ImpliedVol(OptionRight.Call, 150, 100, 100, 1, 0.05, 0).Should().BeNull();
        BlackScholes.ImpliedVol(OptionRight.Put, 0.0000001, 100, 50, 1, 0.05, 0).Should().NotBeNull();
    }

    private static readonly Gen<(double S, double K, double T, double R, double Q, double Sigma)> Inputs =
        from s in Gen.Choose(10, 1000)
        from moneyness in Gen.Choose(50, 150)
        from days in Gen.Choose(1, 730)
        from r in Gen.Choose(0, 80)
        from q in Gen.Choose(0, 40)
        from vol in Gen.Choose(5, 150)
        select ((double)s, s * moneyness / 100.0, days / 365.0, r / 1000.0, q / 1000.0, vol / 100.0);

    [Property(MaxTest = 500)]
    public Property Put_call_parity_holds() => Prop.ForAll(Inputs.ToArbitrary(), x =>
    {
        var c = BlackScholes.Price(OptionRight.Call, x.S, x.K, x.T, x.R, x.Q, x.Sigma);
        var p = BlackScholes.Price(OptionRight.Put, x.S, x.K, x.T, x.R, x.Q, x.Sigma);
        var parity = (x.S * Math.Exp(-x.Q * x.T)) - (x.K * Math.Exp(-x.R * x.T));
        return Math.Abs(c - p - parity) < 1e-8 * Math.Max(1, x.S);
    });

    [Property(MaxTest = 300)]
    public Property Implied_vol_recovers_the_input_vol() => Prop.ForAll(Inputs.ToArbitrary(), x =>
    {
        var right = x.K >= x.S ? OptionRight.Call : OptionRight.Put; // price OTM options, as desks do
        var price = BlackScholes.Price(right, x.S, x.K, x.T, x.R, x.Q, x.Sigma);
        var vega = BlackScholes.Compute(right, x.S, x.K, x.T, x.R, x.Q, x.Sigma).Vega;
        if (price < 1e-6 || vega < 1e-4)
        {
            return true; // vol is not identifiable from a price that rounds to zero
        }

        var iv = BlackScholes.ImpliedVol(right, price, x.S, x.K, x.T, x.R, x.Q);
        return iv is not null && Math.Abs(iv.Value - x.Sigma) < 1e-4;
    });

    [Property(MaxTest = 300)]
    public Property Call_delta_is_between_0_and_1_and_gamma_is_positive() => Prop.ForAll(Inputs.ToArbitrary(), x =>
    {
        var g = BlackScholes.Compute(OptionRight.Call, x.S, x.K, x.T, x.R, x.Q, x.Sigma);
        return g.Delta is >= 0 and <= 1 && g.Gamma >= 0 && g.Vega >= 0;
    });
}

public class OccSymbolTests
{
    [Fact]
    public void Formats_and_parses_the_21_character_symbol()
    {
        var sym = new OccSymbol("SPY", new DateOnly(2025, 6, 20), OptionRight.Call, 550m);

        sym.ToString().Should().Be("SPY   250620C00550000").And.HaveLength(21);
        OccSymbol.TryParse("SPY   250620C00550000", out var parsed).Should().BeTrue();
        parsed.Should().Be(sym);
        OccSymbol.TryParse("NVDA250117P00122500", out var compact).Should().BeTrue();
        compact.Strike.Should().Be(122.5m);
        compact.Right.Should().Be(OptionRight.Put);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SPY")]
    [InlineData("SPY   251320C00550000")]
    [InlineData("SPY   250620X00550000")]
    [InlineData("TOOLONGROOT250620C00550000")]
    public void Rejects_malformed_symbols(string text) => OccSymbol.TryParse(text, out _).Should().BeFalse();
}
