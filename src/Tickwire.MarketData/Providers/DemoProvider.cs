namespace Tickwire.MarketData.Providers;

/// <summary>
/// A keyless stand-in for a real feed, so the whole path (credentials form → server connection → streaming → chain
/// columns) can be exercised without a paid account. It samples a reference price per contract (the host passes the
/// simulated theo) and publishes a jittered NBBO around it, the way an outside market would disagree slightly.
/// </summary>
public sealed class DemoProvider(Func<string, (decimal Theo, double Iv, double Delta)?> reference) : IOptionFeedProvider
{
    public ProviderInfo Info { get; } = new(
        "demo",
        "Demo feed",
        "No account needed. Simulated outside quotes to try the integration end to end.",
        "In-process",
        "https://github.com/danialtoor/tickwire/blob/main/docs/market-data.md",
        "",
        [new("apiKey", "API key (anything works)", false, "demo", "demo")],
        ["NBBO quotes", "Implied vol", "Delta"],
        "#2dd4bf",
        Verified: true);

    public async Task RunAsync(IReadOnlyDictionary<string, string> credentials, FeedSubscription subscription, IFeedSink sink,
        CancellationToken cancellationToken)
    {
        sink.OnStatus($"Demo feed streaming {subscription.Contracts.Count} contracts");
        var random = new Random();
        var contracts = subscription.Contracts.Select(c => c.ToString()).ToArray();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            // Refresh a random slice each tick, like a real feed where only some contracts update.
            for (var i = 0; i < Math.Max(1, contracts.Length / 4); i++)
            {
                var occ = contracts[random.Next(contracts.Length)];
                if (reference(occ) is not { } r || r.Theo <= 0.01m)
                {
                    continue;
                }

                var mid = r.Theo * (1 + (decimal)((random.NextDouble() - 0.5) * 0.04));
                var half = Math.Max(0.01m, Math.Round(mid * 0.02m, 2));
                var bid = Math.Max(0, Math.Round(mid - half, 2));
                sink.OnQuote(new ExternalQuote(occ, bid == 0 ? null : bid, Math.Round(mid + half, 2), random.Next(1, 200), random.Next(1, 200),
                    null, r.Iv * (1 + ((random.NextDouble() - 0.5) * 0.02)), r.Delta, DateTime.UtcNow, "demo"));
            }
        }
    }
}
