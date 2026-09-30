using System.Net.Http.Json;
using FluentAssertions;
using Tickwire.Api.Endpoints;
using Tickwire.Api.Services;

namespace Tickwire.Integration.Tests;

/// <summary>A receive-only drop copy session sees every ExecutionReport the trading session gets, flagged 797=Y.</summary>
[Collection("api")]
public sealed class DropCopyTests(ApiFactory factory)
{
    [Fact]
    public async Task Drop_copy_receives_copies_of_every_execution_report_and_rejects_orders()
    {
        var (http, _) = await factory.NewGuestAsync();
        var trading = (await (await http.PostAsync("/api/connect", null)).Content.ReadFromJsonAsync<ConnectInfo>(ApiFactory.Json))!;
        var dropResp = await http.PostAsync("/api/connect?role=dropcopy", null);
        dropResp.EnsureSuccessStatusCode();
        var drop = (await dropResp.Content.ReadFromJsonAsync<ConnectInfo>(ApiFactory.Json))!;
        drop.SenderCompID.Should().StartWith("DC-").And.NotBe(trading.SenderCompID);
        drop.Role.Should().Be("dropcopy");

        var chain = (await http.GetFromJsonAsync<ChainDto>("/api/chain/SPY", ApiFactory.Json))!;
        var row = chain.Rows[chain.Rows.Count / 2];

        using var copy = new QuickFixClient(factory.FixPort, drop.SenderCompID, drop.TargetCompID);
        using var client = new QuickFixClient(factory.FixPort, trading.SenderCompID, trading.TargetCompID);
        copy.Start();
        client.Start();
        await Until(() => copy.LoggedOn && client.LoggedOn);

        var order = QuickFixClient.Order("DC-TEST-1", 2, row.Call!.Ask!.Value + 0.10m);
        order.Set(new QuickFix.Fields.StrikePrice(row.Strike));
        order.Set(new QuickFix.Fields.MaturityDate(chain.Expiry.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)));
        client.Send(order);

        await Until(() => client.AppMessages.Any(m => m.Header.GetString(35) == "8" && m.GetString(39) == "2"));
        await Until(() => copy.AppMessages.Count(m => m.Header.GetString(35) == "8") >= client.AppMessages.Count(m => m.Header.GetString(35) == "8"));

        var originals = client.AppMessages.Where(m => m.Header.GetString(35) == "8").ToList();
        var copies = copy.AppMessages.Where(m => m.Header.GetString(35) == "8").ToList();
        copies.Select(m => m.GetString(17)).Should().Equal(originals.Select(m => m.GetString(17)), "same ExecIDs, same order");
        copies.Should().OnlyContain(m => m.GetString(797) == "Y" && m.GetString(11) == "DC-TEST-1");
        originals.Should().OnlyContain(m => !m.IsSetField(797));

        // Orders on the drop copy are refused with a BusinessMessageReject.
        copy.Send(QuickFixClient.Order("DC-TEST-2", 1, 1m));
        await Until(() => copy.AppMessages.Any(m => m.Header.GetString(35) == "j"));
        copy.AppMessages.Single(m => m.Header.GetString(35) == "j").GetString(58).Should().Contain("drop copy");
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
