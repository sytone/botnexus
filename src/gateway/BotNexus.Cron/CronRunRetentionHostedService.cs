using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron;

/// <summary>
/// Background service that periodically purges completed/failed cron run records older than
/// the configured retention threshold. Prevents unbounded SQLite growth from frequent cron jobs.
/// </summary>
public sealed class CronRunRetentionHostedService(
    ICronStore cronStore,
    IOptions<CronRunRetentionOptions> optionsAccessor,
    ILogger<CronRunRetentionHostedService> logger) : BackgroundService
{
    private CronRunRetentionOptions Options => optionsAccessor.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var purged = await RunRetentionOnceAsync(stoppingToken).ConfigureAwait(false);
                if (purged > 0)
                {
                    logger.LogInformation(
                        "Cron run retention: purged {Count} run(s) older than {Days} days.",
                        purged, Options.RetentionDays);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cron run retention iteration failed.");
            }

            var delay = Options.CheckInterval > TimeSpan.Zero
                ? Options.CheckInterval
                : TimeSpan.FromHours(1);

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes a single retention pass. Exposed for testing.
    /// </summary>
    internal async Task<int> RunRetentionOnceAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Options.RetentionDays);
        return await cronStore.PurgeRunsOlderThanAsync(cutoff, ct).ConfigureAwait(false);
    }
}
