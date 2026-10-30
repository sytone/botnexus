using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Normalizes legacy SQLite tool data in one set-based transaction after host readiness.
/// A completion record commits with the data, so completed work is not rediscovered on restart.
/// </summary>
public sealed class LegacyToolInvocationBackfillHostedService : BackgroundService
{
    internal const int BatchSize = 100;
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    private readonly SqliteSessionStore? _store;
    private readonly Func<SqliteSessionStore, LegacyToolMigrationReport>? _runMigration;
    private readonly Func<SqliteSessionStore, int, LegacyToolInvocationBackfillReport> _runBatch;
    private readonly Func<SqliteSessionStore, int, long, LegacyToolPayloadCleanupReport> _runCleanupBatch;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly LegacyToolInvocationBackfillMetrics _metrics;
    private readonly LegacyToolPayloadCleanupMetrics _cleanupMetrics;
    private readonly ILogger<LegacyToolInvocationBackfillHostedService> _logger;

    /// <summary>
    /// Creates the production worker. Non-SQLite session stores intentionally make the worker inert.
    /// </summary>
    public LegacyToolInvocationBackfillHostedService(
        ISessionStore sessionStore,
        ILogger<LegacyToolInvocationBackfillHostedService> logger,
        IMetrics? metrics = null)
        : this(
            sessionStore,
            static (store, batchSize) => store.BackfillLegacyToolInvocations(batchSize),
            static (store, batchSize, afterInvocationId) => store.CleanupLegacyToolPayloads(batchSize, afterInvocationId),
            Task.Delay,
            metrics,
            logger)
    {
        _runMigration = static store => store.MigrateLegacyToolData();
    }

    internal LegacyToolInvocationBackfillHostedService(
        ISessionStore sessionStore,
        Func<SqliteSessionStore, LegacyToolMigrationReport> runMigration,
        Func<TimeSpan, CancellationToken, Task> delay,
        IMetrics? metrics,
        ILogger<LegacyToolInvocationBackfillHostedService> logger)
        : this(sessionStore, static (_, _) => new(0,0,0,false,true),
            static (_,_,_) => new(0,0,0,-1,-1,false), delay, metrics, logger)
    {
        _runMigration = runMigration;
    }

    internal LegacyToolInvocationBackfillHostedService(
        ISessionStore sessionStore,
        Func<SqliteSessionStore, int, LegacyToolInvocationBackfillReport> runBatch,
        Func<SqliteSessionStore, int, long, LegacyToolPayloadCleanupReport> runCleanupBatch,
        Func<TimeSpan, CancellationToken, Task> delay,
        IMetrics? metrics,
        ILogger<LegacyToolInvocationBackfillHostedService> logger)
    {
        _store = sessionStore as SqliteSessionStore;
        _runBatch = runBatch;
        _runCleanupBatch = runCleanupBatch;
        _delay = delay;
        _metrics = new LegacyToolInvocationBackfillMetrics(metrics);
        _cleanupMetrics = new LegacyToolPayloadCleanupMetrics(metrics);
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

        long cleanupCursor = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan? nextDelay = null;
            try
            {
                if (_runMigration is not null)
                {
                    LegacyToolMigrationReport migration;
                    try { migration = _runMigration(_store); }
                    catch
                    {
                        _metrics.RecordFailure();
                        _cleanupMetrics.RecordFailure();
                        throw;
                    }
                    _metrics.RecordCommitted(migration.Backfill);
                    _cleanupMetrics.RecordCommitted(migration.Cleanup);
                    _logger.LogInformation("Legacy tool migration committed: {LinkedRows} linked history rows, {CleanedRows} cleaned rows, {ClearedBytes} duplicate bytes cleared.",
                        migration.Backfill.LinkedRows, migration.Cleanup.CleanedRows, migration.Cleanup.ClearedBytes);
                    return;
                }
                LegacyToolInvocationBackfillReport report;
                try
                {
                    report = _runBatch(_store, BatchSize);
                }
                catch
                {
                    _metrics.RecordFailure();
                    throw;
                }
                _metrics.RecordCommitted(report);
                if (report.HasMore)
                {
                    nextDelay = BatchDelay;
                }
                else
                {
                    LegacyToolPayloadCleanupReport cleanup;
                    try
                    {
                        cleanup = _runCleanupBatch(_store, BatchSize, cleanupCursor);
                    }
                    catch
                    {
                        _cleanupMetrics.RecordFailure();
                        throw;
                    }
                    cleanupCursor = cleanup.LastScannedInvocationId;
                    _cleanupMetrics.RecordCommitted(cleanup);
                    _logger.LogInformation(
                        "Legacy tool payload cleanup batch scanned {ScannedInvocations} invocation(s), cleaned {CleanedInvocations} invocation(s) and {CleanedRows} row(s), cleared {ClearedBytes} byte(s), and removed {OrphanedInvocationsDeleted} orphaned invocation(s); more candidate work: {HasMore}.",
                        cleanup.ScannedInvocations,
                        cleanup.CleanedInvocations,
                        cleanup.CleanedRows,
                        cleanup.ClearedBytes,
                        cleanup.OrphanedInvocationsDeleted,
                        cleanup.HasMore);
                    if (!cleanup.HasMore)
                        return;
                    nextDelay = BatchDelay;
                }
            }
            catch (Exception ex)
            {

                if (ex is SqliteException { SqliteErrorCode: 13 } full)
                {
                    var capacity = SqliteCapacityObservation.FromFailure(full);
                    // Never log exception text here: paths and SQL/payloads may occur in it.
                    _logger.LogWarning(
                        "Legacy batch failed with SQLITE_FULL ({SqliteErrorCode}/{SqliteExtendedErrorCode}); observed constraints (not proven cause): PageLimit={PageLimitObserved}, MainVolumeFull={MainVolumeFullObserved}, TempVolumeFull={TempVolumeFullObserved}; PageCount={PageCount}, MaxPageCount={MaxPageCount}, PageSize={PageSize}, FreeListCount={FreeListCount}, TempStore={TempStore}, MainVolumeAvailableBytes={MainVolumeAvailableBytes}, TempVolumeAvailableBytes={TempVolumeAvailableBytes}. Missing observations and actual failure cause are unknown. Check database page limit and main/temp volume capacity or quotas; retrying in {RetrySeconds} seconds without vacuum or deletion.",
                        full.SqliteErrorCode, full.SqliteExtendedErrorCode,
                        capacity?.PageLimitObserved, capacity?.MainVolumeFullObserved, capacity?.TempVolumeFullObserved,
                        capacity?.PageCount, capacity?.MaxPageCount, capacity?.PageSize,
                        capacity?.FreeListCount, capacity?.TempStore,
                        capacity?.MainVolumeAvailableBytes, capacity?.TempVolumeAvailableBytes,
                        RetryDelay.TotalSeconds);
                }
                else
                {
                    _logger.LogWarning(ex, "Legacy tool invocation backfill or payload cleanup batch failed; retrying later.");
                }
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
