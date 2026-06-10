using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Background service that periodically purges completed webhook run records older than
/// the configured retention threshold. Prevents unbounded SQLite growth from active
/// webhook integrations.
/// </summary>
public sealed class WebhookRunRetentionHostedService(
    IWebhookRunStore runStore,
    IOptions<WebhookRunRetentionOptions> optionsAccessor,
    ILogger<WebhookRunRetentionHostedService> logger) : BackgroundService
{
    private WebhookRunRetentionOptions Options => optionsAccessor.Value;

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
                        "Webhook run retention: purged {Count} run(s) older than {Days} days.",
                        purged, Options.RetentionDays);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Webhook run retention iteration failed.");
            }

            var delay = Options.CheckInterval > TimeSpan.Zero
                ? Options.CheckInterval
                : TimeSpan.FromHours(1);
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a single retention pass. Returns the count of purged runs.
    /// Exposed for testability.
    /// </summary>
    public async Task<int> RunRetentionOnceAsync(CancellationToken cancellationToken = default)
    {
        var retentionDays = Options.RetentionDays;
        if (retentionDays <= 0)
            return 0;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        return await runStore.PurgeOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
    }
}
