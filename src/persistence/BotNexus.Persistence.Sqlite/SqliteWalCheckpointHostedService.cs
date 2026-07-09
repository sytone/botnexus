using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Step 3/3 of SQLite WAL maintenance (#1438): a lightweight <see cref="BackgroundService"/> that
/// periodically runs a <c>PASSIVE</c> WAL checkpoint against every registered SQLite database and,
/// on graceful shutdown, a <c>TRUNCATE</c> checkpoint to reclaim the <c>-wal</c> file.
/// <list type="bullet">
///   <item>Uses <see cref="SqliteCheckpointMode.Passive"/> for the periodic sweep so it never
///   blocks on active readers/writers (the OpenClaw <c>1a2e41850092</c> behaviour).</item>
///   <item>Uses <see cref="SqliteCheckpointMode.Truncate"/> once on <see cref="StopAsync"/> to
///   truncate the WAL and reclaim disk before the process exits.</item>
///   <item>Skips databases detected as network-mounted via the shared <see cref="INetworkPathDetector"/>
///   from Step 1 - those are on rollback journaling and have no WAL to checkpoint.</item>
///   <item>Opens a short-lived connection per database per sweep and disposes it immediately, so the
///   service never holds a long-lived handle that could itself block reclamation.</item>
/// </list>
/// The set of databases is enumerated from <see cref="ISqliteDatabaseRegistry"/>, populated at DI
/// wiring time by each store's registration.
/// </summary>
public sealed class SqliteWalCheckpointHostedService : BackgroundService
{
    private readonly ISqliteDatabaseRegistry _registry;
    private readonly INetworkPathDetector _networkPathDetector;
    private readonly SqliteWalCheckpointOptions _options;
    private readonly ILogger<SqliteWalCheckpointHostedService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the hosted checkpoint service.</summary>
    public SqliteWalCheckpointHostedService(
        ISqliteDatabaseRegistry registry,
        INetworkPathDetector networkPathDetector,
        IOptions<SqliteWalCheckpointOptions> options,
        ILogger<SqliteWalCheckpointHostedService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _networkPathDetector = networkPathDetector ?? throw new ArgumentNullException(nameof(networkPathDetector));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value
            ?? new SqliteWalCheckpointOptions();
        _logger = logger ?? NullLogger<SqliteWalCheckpointHostedService>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.Interval;
        _logger.LogInformation(
            "SQLite WAL checkpoint service started; periodic PASSIVE checkpoint interval is {IntervalMinutes} min.",
            interval.TotalMinutes);

        using var timer = new PeriodicTimer(interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await CheckpointAllAsync(SqliteCheckpointMode.Passive, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown; the TRUNCATE sweep happens in StopAsync.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // First let the base class signal ExecuteAsync's stoppingToken and unwind the loop.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Then reclaim WAL space with a TRUNCATE sweep. Use the caller's shutdown token so a
        // hung reclamation cannot stall process exit indefinitely.
        _logger.LogInformation("SQLite WAL checkpoint service stopping; running shutdown TRUNCATE sweep.");
        await CheckpointAllAsync(SqliteCheckpointMode.Truncate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a single checkpoint sweep across every registered database. Public so tests can drive a
    /// deterministic sweep without waiting on the timer.
    /// </summary>
    internal async Task CheckpointAllAsync(SqliteCheckpointMode mode, CancellationToken cancellationToken)
    {
        foreach (var databasePath in _registry.GetDatabasePaths())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (_networkPathDetector.IsNetworkPath(databasePath))
            {
                _logger.LogDebug(
                    "Skipping WAL checkpoint for network-mounted database {DatabasePath} (rollback journaling, no WAL).",
                    databasePath);
                continue;
            }

            try
            {
                await CheckpointOneAsync(databasePath, mode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single bad/locked database must never take down the sweep or the host.
                _logger.LogWarning(
                    ex,
                    "WAL {Mode} checkpoint failed for database {DatabasePath}; continuing with remaining databases.",
                    mode, databasePath);
            }
        }
    }

    private async Task CheckpointOneAsync(string databasePath, SqliteCheckpointMode mode, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            // Do not create the file if it is not there - nothing to checkpoint.
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteWalMaintenance.CheckpointAsync(connection, mode, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("WAL {Mode} checkpoint completed for database {DatabasePath}.", mode, databasePath);
    }
}
