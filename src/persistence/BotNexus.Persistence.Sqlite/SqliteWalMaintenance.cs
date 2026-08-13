using System.Globalization;
using System.IO.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Shared, filesystem-aware SQLite journal-mode maintenance for every BotNexus SQLite store
/// (#1436). Consolidates the previously-duplicated inline <c>PRAGMA journal_mode = WAL</c>
/// blocks into a single tested helper that:
/// <list type="bullet">
///   <item>Applies <c>journal_mode = WAL</c> on local disk for concurrent reader/writer
///   throughput, but falls back to <c>journal_mode = DELETE</c> (rollback journaling) when the
///   database path resolves to a network mount, because WAL's shared-memory index is unreliable
///   on NFS/SMB/UNC and can corrupt or silently disable WAL.</item>
///   <item>Bounds <c>-wal</c> growth with an explicit <c>PRAGMA wal_autocheckpoint = N</c>
///   (default 1000 pages) whenever WAL is engaged.</item>
///   <item>Caps the on-disk <c>-wal</c> file size with <c>PRAGMA journal_size_limit = N</c>
///   (default 64 MiB). <c>wal_autocheckpoint</c> and PASSIVE checkpoints recycle WAL space
///   <i>in place</i> and never shrink the file, so without this cap a transiently-blocked
///   checkpoint can leave a multi-gigabyte <c>-wal</c> parked at its high-water mark until the
///   process exits (#2370).</item>
///   <item>Verifies the <b>effective</b> journal mode by re-reading <c>PRAGMA journal_mode;</c>
///   and logs a warning (and surfaces it via <see cref="JournalModeResult.Applied"/>) when the
///   requested mode did not take.</item>
/// </list>
/// The <see cref="CheckpointAsync"/> method is provided for the Step 3 periodic checkpoint
/// service to call; this helper deliberately contains <b>no</b> timer or hosted-service loop.
/// </summary>
/// <remarks>
/// <c>busy_timeout</c> is intentionally <b>not</b> managed here: it is a per-connection setting
/// that must be re-applied on every fresh connection, so each store keeps applying it in its own
/// connection path (see #1450). This helper only owns the database-level journal-mode concern.
/// <para>
/// Every pragma this helper issues goes through <see cref="TryExecutePragmaCoreAsync{T}"/>, the
/// single disposal-race seam described there (#3124).
/// </para>
/// </remarks>
public sealed class SqliteWalMaintenance
{
    /// <summary>Default <c>wal_autocheckpoint</c> page threshold (SQLite's own default is 1000).</summary>
    public const int DefaultWalAutocheckpoint = 1000;

    /// <summary>
    /// Default <c>journal_size_limit</c> in bytes (64 MiB). Chosen to be comfortably larger than a
    /// healthy steady-state WAL - so ordinary commits never pay a truncation cost - while still
    /// reclaiming pathological high-water marks left behind by a checkpoint that a long-running
    /// reader transiently blocked.
    /// </summary>
    public const long DefaultJournalSizeLimitBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Sentinel for <c>journal_size_limit</c> meaning "no bound" - SQLite's own default. Pass this
    /// only when a store deliberately wants an unbounded <c>-wal</c>; it reintroduces the growth
    /// documented in #2370.
    /// </summary>
    public const long UnlimitedJournalSizeLimit = -1L;

    private readonly INetworkPathDetector _networkPathDetector;
    private readonly ILogger<SqliteWalMaintenance> _logger;

    /// <summary>
    /// Creates a maintenance helper with an explicit network-path detector. Prefer this overload
    /// in tests so the network-mount decision can be mocked deterministically.
    /// </summary>
    public SqliteWalMaintenance(INetworkPathDetector networkPathDetector, ILogger<SqliteWalMaintenance>? logger = null)
    {
        _networkPathDetector = networkPathDetector ?? throw new ArgumentNullException(nameof(networkPathDetector));
        _logger = logger ?? NullLogger<SqliteWalMaintenance>.Instance;
    }

    /// <summary>
    /// Creates a maintenance helper backed by the default <see cref="NetworkPathDetector"/> over the
    /// supplied <see cref="IFileSystem"/> (or the real filesystem when <paramref name="fileSystem"/>
    /// is <c>null</c>). This is the overload production stores use.
    /// </summary>
    public SqliteWalMaintenance(IFileSystem? fileSystem = null, ILogger<SqliteWalMaintenance>? logger = null)
        : this(new NetworkPathDetector(fileSystem ?? new FileSystem()), logger)
    {
    }

    /// <summary>
    /// Applies the appropriate journal mode to an <b>already-open</b> <paramref name="connection"/>
    /// based on where <paramref name="databasePath"/> lives, then verifies the effective mode.
    /// Call this once from a store's init/EnsureCreated path in place of the old inline
    /// <c>PRAGMA journal_mode = WAL</c>.
    /// </summary>
    /// <param name="connection">An open SQLite connection to the target database.</param>
    /// <param name="databasePath">The on-disk path of the database file, used for network detection.</param>
    /// <param name="walAutocheckpoint">Page threshold for <c>wal_autocheckpoint</c> when WAL engages.</param>
    /// <param name="journalSizeLimitBytes">Byte cap for <c>journal_size_limit</c> when WAL engages.
    /// Defaults to <see cref="DefaultJournalSizeLimitBytes"/> (64 MiB). Pass
    /// <see cref="UnlimitedJournalSizeLimit"/> (-1) to opt out of the bound entirely.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="JournalModeResult"/> describing the requested vs effective mode. When
    /// the connection's native handle is already released the pragmas are skipped and the result
    /// reports an empty effective mode, so <see cref="JournalModeResult.Applied"/> is <c>false</c>
    /// rather than falsely claiming a verified mode (#3124).</returns>
    public async Task<JournalModeResult> ApplyJournalModeAsync(
        SqliteConnection connection,
        string databasePath,
        int walAutocheckpoint = DefaultWalAutocheckpoint,
        long journalSizeLimitBytes = DefaultJournalSizeLimitBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        if (walAutocheckpoint < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(walAutocheckpoint), walAutocheckpoint, "wal_autocheckpoint must be non-negative.");
        }

        // -1 is SQLite's documented "unlimited" sentinel; anything below that is meaningless and
        // would be silently coerced by SQLite, hiding a configuration mistake.
        if (journalSizeLimitBytes < UnlimitedJournalSizeLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(journalSizeLimitBytes),
                journalSizeLimitBytes,
                "journal_size_limit must be -1 (unlimited) or a non-negative byte count.");
        }

        var isNetwork = _networkPathDetector.IsNetworkPath(databasePath);
        // WAL needs shared-memory that network filesystems cannot reliably provide; rollback
        // journaling (DELETE) is the safe, portable fallback there.
        var requestedMode = isNetwork ? "delete" : "wal";

        var modeExecuted = await TryExecutePragmaAsync(
            connection,
            $"PRAGMA journal_mode = {requestedMode};",
            cancellationToken).ConfigureAwait(false);

        int? appliedAutocheckpoint = null;
        long? appliedJournalSizeLimit = null;
        if (modeExecuted && !isNetwork)
        {
            // Bound -wal growth so a long-lived writer cannot let the WAL balloon unbounded.
            if (await TryExecutePragmaAsync(
                    connection,
                    $"PRAGMA wal_autocheckpoint = {walAutocheckpoint};",
                    cancellationToken).ConfigureAwait(false))
            {
                appliedAutocheckpoint = walAutocheckpoint;
            }

            // wal_autocheckpoint recycles WAL space in place but never shrinks the file, so cap the
            // on-disk size too: SQLite truncates back to this bound when a fully-checkpointed WAL
            // resets, which is the only thing that reclaims a pathological high-water mark short of
            // closing the database (#2370).
            if (await TryExecutePragmaAsync(
                    connection,
                    $"PRAGMA journal_size_limit = {journalSizeLimitBytes.ToString(CultureInfo.InvariantCulture)};",
                    cancellationToken).ConfigureAwait(false))
            {
                appliedJournalSizeLimit = journalSizeLimitBytes;
            }
        }

        var (verified, effectiveMode) =
            await QueryEffectiveJournalModeAsync(connection, cancellationToken).ConfigureAwait(false);

        var result = new JournalModeResult(requestedMode, effectiveMode, isNetwork, appliedAutocheckpoint, appliedJournalSizeLimit);

        if (!verified)
        {
            // The native handle went away before the effective mode could be re-read, so the mode
            // was never verified. EffectiveMode is deliberately left empty - never the requested
            // mode - which keeps JournalModeResult.Applied false: an unverified mode must not be
            // reported to the caller as a verified one (#3124).
            _logger.LogDebug(
                "SQLite journal_mode verification for {DatabasePath} was skipped: the connection's native " +
                "handle was already released, so 'PRAGMA journal_mode;' could not be re-read. " +
                "Requested '{Requested}'.",
                databasePath, requestedMode);
        }
        else if (!result.Applied)
        {
            _logger.LogWarning(
                "SQLite journal_mode for {DatabasePath} did not take: requested '{Requested}' but effective mode is " +
                "'{Effective}'. NetworkPath={IsNetwork}. Concurrency behaviour may be degraded.",
                databasePath, requestedMode, effectiveMode, isNetwork);
        }
        else
        {
            _logger.LogDebug(
                "SQLite journal_mode for {DatabasePath} set to '{Effective}' (network={IsNetwork}, " +
                "wal_autocheckpoint={AutoCp}, journal_size_limit={JournalSizeLimit}).",
                databasePath, effectiveMode, isNetwork, appliedAutocheckpoint, appliedJournalSizeLimit);
        }

        return result;
    }

    /// <summary>
    /// Runs a WAL checkpoint against an <b>already-open</b> <paramref name="connection"/>. Exposed
    /// for the Step 3 periodic checkpoint service and for graceful-shutdown reclamation; this helper
    /// does not schedule checkpoints itself. A no-op (returns without error) when the database is not
    /// in WAL mode - <c>wal_checkpoint</c> is harmless on rollback-journalled databases - and equally
    /// a no-op when the connection's native handle has already been released (#3124).
    /// </summary>
    /// <param name="connection">An open SQLite connection to the target database.</param>
    /// <param name="mode">Checkpoint aggressiveness - see <see cref="SqliteCheckpointMode"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task CheckpointAsync(
        SqliteConnection connection,
        SqliteCheckpointMode mode = SqliteCheckpointMode.Passive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var modeKeyword = mode switch
        {
            SqliteCheckpointMode.Passive => "PASSIVE",
            SqliteCheckpointMode.Truncate => "TRUNCATE",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported checkpoint mode."),
        };

        await TryExecutePragmaCoreAsync<object?>(
            connection,
            $"PRAGMA wal_checkpoint({modeKeyword});",
            static async (cmd, ct) =>
            {
                // wal_checkpoint returns a result row (busy, log, checkpointed); execute as a reader
                // so the row is consumed, but we do not need the values for a fire-and-forget
                // checkpoint.
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    // drain
                }

                return (object?)null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(bool Verified, string Mode)> QueryEffectiveJournalModeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var (executed, value) = await TryExecutePragmaCoreAsync<object?>(
            connection,
            "PRAGMA journal_mode;",
            static (cmd, ct) => cmd.ExecuteScalarAsync(ct),
            cancellationToken).ConfigureAwait(false);

        if (!executed)
        {
            return (false, string.Empty);
        }

        return (true, value?.ToString()?.ToLowerInvariant() ?? string.Empty);
    }

    /// <summary>
    /// The single seam through which <b>every</b> pragma issued by this helper is executed (#3124).
    /// </summary>
    /// <remarks>
    /// A managed <see cref="SqliteConnection"/> can report <c>Open</c> while its underlying
    /// <c>SQLitePCL.sqlite3</c> handle has already been released (observed under parallel load in
    /// the core gate). Preparing a statement against that handle throws
    /// <see cref="ObjectDisposedException"/> out of what is only best-effort per-connection tuning,
    /// on a connection that is going away regardless. The same lesson was learned at the
    /// <c>busy_timeout</c> site in <see cref="SqliteConnectionFactory"/> (#2977) and applied there
    /// alone, leaving these sibling pragmas unguarded; expressing the guard once here means a
    /// pragma added later inherits it instead of repeating the defect.
    /// <para>
    /// The handle pre-check is cheap; the <see cref="ObjectDisposedException"/> catch closes the
    /// race window between that check and the prepare. <b>Only</b>
    /// <see cref="ObjectDisposedException"/> is swallowed - any other exception is a genuine fault
    /// (a real journal-mode misconfiguration, say) and deliberately propagates to the caller.
    /// </para>
    /// </remarks>
    /// <returns><c>false</c> when the pragma was skipped because the native handle was already
    /// released; otherwise <c>true</c> together with the delegate's result.</returns>
    private static async Task<(bool Executed, T? Result)> TryExecutePragmaCoreAsync<T>(
        SqliteConnection connection,
        string commandText,
        Func<SqliteCommand, CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken)
    {
        if (connection.Handle is null || connection.Handle.IsInvalid || connection.Handle.IsClosed)
        {
            return (false, default);
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = commandText;
            var result = await execute(cmd, cancellationToken).ConfigureAwait(false);
            return (true, result);
        }
        catch (ObjectDisposedException)
        {
            // Lost the race with disposal between the handle pre-check and the prepare (#3124).
            return (false, default);
        }
    }

    /// <summary>
    /// Convenience over <see cref="TryExecutePragmaCoreAsync{T}"/> for pragmas whose result value is
    /// not needed. It routes through the one guard rather than restating it.
    /// </summary>
    private static async Task<bool> TryExecutePragmaAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        var (executed, _) = await TryExecutePragmaCoreAsync(
            connection,
            commandText,
            static (cmd, ct) => cmd.ExecuteNonQueryAsync(ct),
            cancellationToken).ConfigureAwait(false);
        return executed;
    }
}
