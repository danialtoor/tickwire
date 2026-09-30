using Tickwire.Venue;

namespace Tickwire.Api.Services;

/// <summary>Runs the expiry roll once a minute so the chain always shows the next four Fridays.</summary>
public sealed partial class ChainRollService(ExpiryRoller roller, TimeProvider time, ILogger<ChainRollService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            var result = roller.Roll(time.GetUtcNow().UtcDateTime);
            if (result.Delisted.Count + result.Listed.Count > 0)
            {
                Rolled(logger, string.Join(", ", result.Delisted), string.Join(", ", result.Listed));
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expiry roll: delisted [{Delisted}], listed [{Listed}]")]
    private static partial void Rolled(ILogger logger, string delisted, string listed);
}
