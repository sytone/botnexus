using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Gradually normalizes legacy SQLite tool-call transcript rows after startup without delaying
/// host readiness. Linked rows are the durable checkpoint, so restarts naturally resume the work.
/// </summary>
public sealed class LegacyToolInvocationBackfillHostedService : BackgroundService
{
    internal const int BatchSize = 100;
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    private readonly SqliteSessionStore? _store;
    private readonly Func<SqliteSessionStore, int, LegacyToolInvocationBackfillReport> _runBatch;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<LegacyToolInvocationBackfillHostedService> _logger;

    /// <summary>
    /// Creates the production worker. Non-SQLite session stores intentionally make the worker inert.
    /// </summary>
    public LegacyToolInvocationBackfillHostedService(
        ISessionStore sessionStore,
        ILogger<LegacyToolInvocationBackfillHostedService> logger)
        : this(sessionStore, static (store, batchSize) => store.BackfillLegacyToolInvocations(batchSize), Task.Delay, logger)
    {
    }

    internal LegacyToolInvocationBackfillHostedService(
        ISessionStore sessionStore,
        Func<SqliteSessionStore, int, LegacyToolInvocationBackfillReport> runBatch,
        Func<TimeSpan, CancellationToken, Task> delay,
        ILogger<LegacyToolInvocationBackfillHostedService> logger)
    {
        _store = sessionStore as SqliteSessionStore;
        _runBatch = runBatch;
        _delay = delay;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_store is null)
            return;

        try
        {
            await _delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan? nextDelay = null;
            try
            {
                var report = _runBatch(_store, BatchSize);
                if (!report.HasMore)
                    return;

                nextDelay = BatchDelay;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Legacy tool invocation backfill batch failed; retrying later.");
                nextDelay = RetryDelay;
            }

            try
            {
                await _delay(nextDelay.Value, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
