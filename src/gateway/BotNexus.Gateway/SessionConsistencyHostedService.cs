using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway;

/// <summary>
/// Background driver for <see cref="SessionConsistencyChecker"/>. Runs an initial pass a
/// short, configurable delay after host startup - so agent registration, store hydration,
/// and interrupted-turn recovery settle first - then repeats at a bounded cadence
/// (issue #2046).
/// </summary>
/// <remarks>
/// The service is opt-out via <see cref="SessionConsistencyOptions.Enabled"/> and honours
/// <see cref="SessionConsistencyOptions.DryRun"/> for report-only operation. Each pass is
/// idempotent and bounded; a failed iteration is logged and retried on the next tick without
/// tearing down the host.
/// </remarks>
public sealed class SessionConsistencyHostedService(
    SessionConsistencyChecker checker,
    IOptions<SessionConsistencyOptions> optionsAccessor,
    ILogger<SessionConsistencyHostedService> logger) : BackgroundService
{
    private readonly SessionConsistencyChecker _checker = checker;
    private readonly ILogger<SessionConsistencyHostedService> _logger = logger;

    private SessionConsistencyOptions Options => optionsAccessor.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Options.Enabled)
        {
            _logger.LogInformation("Session consistency monitor disabled via configuration; not running.");
            return;
        }

        var startupDelay = Options.StartupDelay < TimeSpan.Zero ? TimeSpan.Zero : Options.StartupDelay;
        try
        {
            await Task.Delay(startupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Re-read Enabled each iteration so a config toggle takes effect without restart.
            if (!Options.Enabled)
            {
                _logger.LogInformation("Session consistency monitor disabled via configuration; pausing checks.");
            }
            else
            {
                try
                {
                    await _checker.RunOnceAsync(dryRun: false, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session consistency pass failed; will retry next interval.");
                }
            }

            var interval = Options.CheckInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : Options.CheckInterval;
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
