using System.Net.Http.Json;
using FluentAssertions;
using QuickFix.Fields;
using Tickwire.Api.Endpoints;
using Tickwire.Api.Services;

namespace Tickwire.Integration.Tests;

/// <summary>QuickFIX/n sends a real NewOrderMultileg (35=AB) with a NoLegs group; its dictionary validates our reports.</summary>
[Collection("api")]
public sealed class MultilegInteropTests(ApiFactory factory)
{
    [Fact]
    public async Task QuickFix_vertical_spread_gets_strategy_and_leg_reports()
    {
        var (http, _) = await factory.NewGuestAsync();
        var connect = (await (await http.PostAsync("/api/connect", null)).Content.ReadFromJsonAsync<ConnectInfo>(ApiFactory.Json))!;
        var chain = (await http.GetFromJsonAsync<ChainDto>("/api/chain/SPY", ApiFactory.Json))!;
        var mid = chain.Rows.Count / 2;
        var (low, high) = (chain.Rows[mid], chain.Rows[mid + 1]);
        var limit = low.Call!.Ask!.Value - high.Call!.Bid!.Value + 0.10m; // cushion so a tick of market movement cannot leave it resting

        using var client = new QuickFixClient(factory.FixPort, connect.SenderCompID, connect.TargetCompID);
        client.Start();
        await Until(() => client.LoggedOn);

        var order = new QuickFix.FIX44.NewOrderMultileg(new ClOrdID("QF-SPREAD-1"), new Side(Side.BUY), new Symbol("SPY"),
            new TransactTime(DateTime.UtcNow), new OrdType(OrdType.LIMIT));
        order.Set(new OrderQty(2));
        order.Set(new Price(limit));
        foreach (var (row, side) in new[] { (low, Side.BUY), (high, Side.SELL) })
        {
            var leg = new QuickFix.FIX44.NewOrderMultileg.NoLegsGroup();
            leg.Set(new LegSymbol("SPY"));
            leg.Set(new LegCFICode("OCXXXS"));
            leg.Set(new LegMaturityDate(chain.Expiry.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)));
            leg.Set(new LegStrikePrice(row.Strike));
            leg.Set(new LegRatioQty(1));
            leg.Set(new LegSide(side));
            order.AddGroup(leg);
        }

        client.Send(order);

        await Until(() => client.AppMessages.Count(m => m.IsSetField(442) && m.GetString(442) == "2") >= 2, 15_000);
        var reports = client.AppMessages.Where(m => m.Header.GetString(35) == "8").ToList();
        reports.Should().Contain(m => m.GetString(442) == "3" && m.GetString(150) == "0" && m.GetInt(555) == 2);
        var legReports = reports.Where(m => m.GetString(442) == "2").ToList();
        legReports.Select(m => m.GetDecimal(202)).Should().Contain(low.Strike).And.Contain(high.Strike);
        legReports.Should().Contain(m => m.GetDecimal(202) == low.Strike && m.GetString(54) == "1", "the long leg is bought");
        legReports.Should().Contain(m => m.GetDecimal(202) == high.Strike && m.GetString(54) == "2", "the short leg is sold");
        var strategyFills = reports.Where(m => m.GetString(442) == "3" && m.GetString(150) == "F").ToList();
        strategyFills.Sum(m => m.GetDecimal(32)).Should().Be(2);
        strategyFills.Should().OnlyContain(m => m.GetDecimal(31) <= limit, "a bought spread never pays more than its limit");

        var rows = await http.GetFromJsonAsync<List<OrderRowDto>>("/api/orders", ApiFactory.Json);
        rows!.Single(r => r.Order.ClOrdID == "QF-SPREAD-1").Order.Display.Should().EndWith("C vertical");
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(50);
        }
    }
}
