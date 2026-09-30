using System.Net.Http.Json;
using FluentAssertions;
using QuickFix.Fields;
using Tickwire.Api.Endpoints;
using Tickwire.Api.Services;

namespace Tickwire.Integration.Tests;

/// <summary>ExDestination(100) and the fill tags the router adds, checked by QuickFIX/n's own FIX 4.4 dictionary.</summary>
[Collection("api")]
public sealed class RoutingInteropTests(ApiFactory factory)
{
    [Fact]
    public async Task Directed_order_fills_on_that_exchange_with_LastMkt_commission_and_liquidity()
    {
        var (http, _) = await factory.NewGuestAsync();
        var connect = (await (await http.PostAsync("/api/connect", null)).Content.ReadFromJsonAsync<ConnectInfo>(ApiFactory.Json))!;
        var chain = (await http.GetFromJsonAsync<ChainDto>("/api/chain/SPY", ApiFactory.Json))!;
        var row = chain.Rows[chain.Rows.Count / 2];
        var book = (await http.GetFromJsonAsync<BookDto>($"/api/book/{row.Call!.Id}", ApiFactory.Json))!;
        var nova = book.Venues.Single(v => v.Exchange == "NOVA");

        using var client = new QuickFixClient(factory.FixPort, connect.SenderCompID, connect.TargetCompID);
        client.Start();
        await Until(() => client.LoggedOn);

        var order = QuickFixClient.Order("ROUTE-1", 2, nova.Ask!.Value + 0.10m);
        order.Set(new StrikePrice(row.Strike));
        order.Set(new MaturityDate(chain.Expiry.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)));
        order.Set(new ExDestination("NOVA"));
        client.Send(order);

        try
        {
            await Until(() => client.AppMessages.Any(m => m.Header.GetString(35) == "8" && m.GetString(39) == "2"));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(string.Join("\n", client.AdminOut.Select(m => m.ToString().Replace('\x01', '|'))
                .Concat(client.AppMessages.Select(m => "APP " + m.ToString().Replace('\x01', '|')))));
        }
        var fills = client.AppMessages.Where(m => m.Header.GetString(35) == "8" && m.GetString(150) == "F").ToList();
        fills.Should().NotBeEmpty().And.OnlyContain(m => m.GetString(30) == "NOVA" && m.GetString(851) == "2" && m.GetString(13) == "3");
        fills.Sum(m => m.GetDecimal(12)).Should().Be(2 * 0.15m);
        client.AppMessages.Single(m => m.Header.GetString(35) == "8" && m.GetString(150) == "0").GetString(58)
            .Should().Be("Directed to NOVA (ExDestination)");

        var bad = QuickFixClient.Order("ROUTE-2", 1, 1m);
        bad.Set(new ExDestination("XNYS"));
        client.Send(bad);
        await Until(() => client.AppMessages.Any(m => m.Header.GetString(35) == "j"));
        client.AppMessages.Single(m => m.Header.GetString(35) == "j").GetString(58).Should().Contain("Unknown ExDestination(100)=XNYS");
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
