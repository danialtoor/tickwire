using System.Net.Http.Json;
using FluentAssertions;
using QuickFix.Fields;
using Tickwire.Api.Endpoints;
using Tickwire.Api.Services;

namespace Tickwire.Integration.Tests;

/// <summary>QuickFIX/n as a FIXT 1.1 / FIX 5.0 SP2 client, validating our messages with its own FIXT11 and FIX50SP2 dictionaries.</summary>
[Collection("api")]
public sealed class FixtInteropTests(ApiFactory factory)
{
    [Fact]
    public async Task QuickFix_on_FIXT_1_1_logs_on_and_trades()
    {
        var (http, _) = await factory.NewGuestAsync();
        var resp = await http.PostAsync("/api/connect?version=FIXT.1.1", null);
        resp.EnsureSuccessStatusCode();
        var connect = (await resp.Content.ReadFromJsonAsync<ConnectInfo>(ApiFactory.Json))!;
        connect.BeginString.Should().Be("FIXT.1.1");
        connect.Configs["quickfixn.cfg"].Should().Contain("BeginString=FIXT.1.1").And.Contain("DefaultApplVerID=FIX.5.0SP2")
            .And.Contain("AppDataDictionary=FIX50SP2.xml");

        var chain = (await http.GetFromJsonAsync<ChainDto>("/api/chain/SPY", ApiFactory.Json))!;
        var row = chain.Rows[chain.Rows.Count / 2];

        using var client = new QuickFixClient(factory.FixPort, connect.SenderCompID, connect.TargetCompID, beginString: "FIXT.1.1");
        client.Start();
        await Until(() => client.LoggedOn);
        client.AdminIn.Single(m => m.Header.GetString(35) == "A").GetString(1137).Should().Be("9");

        var order = new QuickFix.FIX50SP2.NewOrderSingle(new ClOrdID("FIXT-1"), new Side(Side.BUY), new TransactTime(DateTime.UtcNow),
            new OrdType(OrdType.LIMIT));
        order.Set(new Symbol("SPY"));
        order.Set(new SecurityType(SecurityType.OPTION));
        order.Set(new PutOrCall(PutOrCall.CALL));
        order.Set(new StrikePrice(row.Strike));
        order.Set(new MaturityDate(chain.Expiry.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)));
        order.Set(new OrderQty(2));
        order.Set(new Price(row.Call!.Ask!.Value + 0.10m));
        order.Set(new TimeInForce(TimeInForce.DAY));
        client.Send(order);

        await Until(() => client.AppMessages.Any(m => m.Header.GetString(35) == "8" && m.GetString(39) == "2"));
        var reports = client.AppMessages.Where(m => m.Header.GetString(35) == "8").ToList();
        reports.Should().OnlyContain(m => m.Header.GetString(8) == "FIXT.1.1" && m.GetString(11) == "FIXT-1");
        reports.Where(m => m.GetString(150) == "F").Sum(m => m.GetDecimal(32)).Should().Be(2);
        client.AdminIn.Should().NotContain(m => m.Header.GetString(35) == "3", "the venue accepted every message");
        client.AdminOut.Should().NotContain(m => m.Header.GetString(35) == "3", "QuickFIX accepted every report against FIX50SP2");
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met");
            }

            await Task.Delay(50);
        }
    }
}
