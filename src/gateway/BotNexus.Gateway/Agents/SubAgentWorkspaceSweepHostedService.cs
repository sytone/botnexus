using System.IO.Abstractions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Background service that periodically (and shortly after gateway startup) runs the age-based
/// sweep of completed sub-agent workspace directories under the persistent agents root
/// (issue #2237). It mirrors the hosted-service style of the other retention services in the
/// gateway and delegates the actual reconciliation to the pure <see cref="SubAgentWorkspaceSweeper"/>.
/// <para>
/// The sweep is opt-out: it is enabled by default with a 24h idle TTL and a 1h safety grace window,
/// as the issue asks for automatic cleanup. It never touches top-level registered agent workspaces
/// (that is #2039's manual, registration-based reconciliation) — it scopes strictly to
/// <c>*--subagent--*</c> husks.
/// </para>
/// </summary>
public sealed class SubAgentWorkspaceSweepHostedService(
    BotNexusHome botNexusHome,
    IFileSystem fileSystem,
    IOptions<SubAgentWorkspaceSweepOptions> optionsAccessor,
    ILogger<SubAgentWorkspaceSweepHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly BotNexusHome _botNexusHome = botNexusHome;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ILogger<SubAgentWorkspaceSweepHostedService> _logger = logger;

    private SubAgentWorkspaceSweepOptions Options => optionsAccessor.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Options.Enabled)
            return;

        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunSweepOnce();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sub-agent workspace sweep iteration failed.");
            }

            var delay = Options.CheckInterval > TimeSpan.Zero
                ? Options.CheckInterval
                : TimeSpan.FromHours(1);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs a single sweep pass and emits one summary log line. Returns the sweep result so callers
    /// (and tests) can inspect the counts. A disabled sweep is a no-op returning the default result.
    /// </summary>
    public SubAgentWorkspaceSweepResult RunSweepOnce()
    {
        var options = Options;
        if (!options.Enabled || options.Retention <= TimeSpan.Zero)
            return default;

        var sweeper = new SubAgentWorkspaceSweeper(_fileSystem, _logger);
        var result = sweeper.Sweep(_botNexusHome.AgentsPath, options.Retention, options.Grace, DateTime.UtcNow);

        _logger.LogInformation(
            "Sub-agent workspace sweep: removed {Removed} directory(ies), reclaimed {BytesReclaimed} bytes, skipped {SkippedRecent} recent/unexpired (retention {RetentionHours}h, grace {GraceMinutes}m).",
            result.Removed,
            result.BytesReclaimed,
            result.SkippedRecent,
            options.RetentionHours,
            options.GraceMinutes);

        return result;
    }
}
