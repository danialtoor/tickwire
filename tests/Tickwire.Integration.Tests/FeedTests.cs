using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Tickwire.Api.Endpoints;
using Tickwire.Api.Services;
using Tickwire.MarketData;

namespace Tickwire.Integration.Tests;

[Collection("api")]
public sealed class FeedTests(ApiFactory factory)
{
    [Fact]
    public async Task Lists_the_supported_providers()
    {
        var providers = await factory.CreateClient().GetFromJsonAsync<List<ProviderInfo>>("/api/feeds/providers", ApiFactory.Json);

        providers!.Select(p => p.Id).Should().BeEquivalentTo(["spiderrock", "databento", "polygon", "tradier", "alpaca", "demo"]);
        providers!.Single(p => p.Id == "databento").Credentials.Should().Contain(c => c.Name == "apiKey" && c.Secret);
    }

    [Fact]
    public async Task Demo_feed_connects_streams_and_disconnects()
    {
        var (http, _) = await factory.NewGuestAsync();

        var status = await (await http.PostAsJsonAsync("/api/feeds/connect", new FeedConnectBody("demo", new() { ["apiKey"] = "demo" })))
            .Content.ReadFromJsonAsync<FeedStatusDto>(ApiFactory.Json);
        status!.Provider.Should().Be("demo");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        FeedSnapshotDto? snapshot;
        do
        {
            await Task.Delay(200);
            snapshot = await http.GetFromJsonAsync<FeedSnapshotDto>("/api/feeds", ApiFactory.Json);
        }
        while (snapshot!.Quotes.Count == 0 && DateTime.UtcNow < deadline);

        snapshot.Status.State.Should().Be("Streaming");
        snapshot.Quotes.Should().NotBeEmpty().And.OnlyContain(q => q.Provider == "demo" && q.Ask > 0);

        (await http.PostAsync("/api/feeds/disconnect", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await http.GetFromJsonAsync<FeedSnapshotDto>("/api/feeds", ApiFactory.Json))!.Status.State.Should().Be("Idle");
    }

    [Fact]
    public async Task Rejects_unknown_providers_and_missing_credentials()
    {
        var (http, _) = await factory.NewGuestAsync();

        (await http.PostAsJsonAsync("/api/feeds/connect", new FeedConnectBody("nope", null))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var missing = await http.PostAsJsonAsync("/api/feeds/connect", new FeedConnectBody("databento", new()));
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("API key");
        (await factory.CreateClient().PostAsJsonAsync("/api/feeds/connect", new FeedConnectBody("demo", null)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
