using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Single source of truth for "how a BotNexus SQLite connection is opened" (#1541).
/// Every SQLite-backed store previously duplicated an identical <c>StateChange</c> Open-handler
/// that applied <c>PRAGMA busy_timeout=5000</c> on every fresh connection; that boilerplate
/// (and the magic <c>5000</c>) is consolidated here so timeout and foreign-key enforcement
/// policies live in exactly one place.
/// </summary>
/// <remarks>
/// <c>busy_timeout</c> and <c>foreign_keys</c> are <b>per-connection</b> settings, so they must
/// be applied on every fresh connection rather than once at database init (unlike
/// the database-level <c>journal_mode</c>, which <see cref="SqliteWalMaintenance"/> owns).
/// Existing-row integrity validation is database-level work: the factory coordinates it under an
/// adjacent lock file and records each successful SQLite schema generation in an adjacent receipt
/// so ordinary opens do not rescan or mutate the store. The factory attaches a
/// <see cref="System.Data.Common.DbConnection.StateChange"/> handler that re-applies connection
/// pragmas whenever the connection transitions to <see cref="System.Data.ConnectionState.Open"/>,
/// including close/reopen cycles. Journal-mode / WAL policy remains the concern of
/// <see cref="SqliteWalMaintenance"/>; a store applies that once against an open connection after
/// obtaining it from this factory.
/// </remarks>
public static class SqliteConnectionFactory
{
    private static readonly ActivitySource ActivitySource = new("BotNexus.Persistence.Sqlite");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> IntegrityLocks = new(StringComparer.Ordinal);

    /// <summary>
    /// Default <c>busy_timeout</c> in milliseconds applied to every BotNexus SQLite connection.
    /// Lets a concurrent cross-process writer wait briefly for a held lock instead of failing
    /// immediately with <c>SQLITE_BUSY</c> (#1450).
    /// </summary>
    public const int DefaultBusyTimeoutMs = 5000;

    /// <summary>
    /// Creates a (not-yet-open) <see cref="SqliteConnection"/> for <paramref name="connectionString"/>
    /// with the standard BotNexus busy-timeout policy attached via a <c>StateChange</c> handler, so
    /// the timeout is (re)applied automatically on every open. Callers open the connection themselves
    /// (synchronously or via <see cref="SqliteConnection.OpenAsync(System.Threading.CancellationToken)"/>).
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="busyTimeoutMs">
    /// The <c>busy_timeout</c> to apply on open, in milliseconds. Defaults to
    /// <see cref="DefaultBusyTimeoutMs"/>.
    /// </param>
    /// <returns>A connection with the busy-timeout Open-handler attached.</returns>
    public static SqliteConnection Create(string connectionString, int busyTimeoutMs = DefaultBusyTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        if (busyTimeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(busyTimeoutMs), busyTimeoutMs, "busy_timeout must be non-negative.");
        }

        var connection = new SqliteConnection(connectionString);
        AttachBusyTimeout(connection, busyTimeoutMs);
        return connection;
    }

    /// <summary>
    /// Creates a connection whose world identity is verified against <paramref name="storeKind"/>
    /// rather than against the kind derived from the file name (#2833). Use this when the store's
    /// file name does not name its kind.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> an overload of <see cref="Create(string, int)"/>. Extensions resolve
    /// this type across load contexts and bind <c>Create</c> by reflection; #2481 was diagnosed from
    /// a <c>MissingMethodException: SqliteConnectionFactory.Create(String, Int32)</c>, and a
    /// name-only <c>GetMethod("Create", ...)</c> lookup throws <c>AmbiguousMatchException</c> the
    /// moment a second <c>Create</c> exists. A distinct name keeps that binding surface single-valued.
    /// </remarks>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="storeKind">The kind the caller believes it is opening (<c>cron</c>, <c>sessions</c>, ...).</param>
    /// <param name="busyTimeoutMs">The <c>busy_timeout</c> to apply on open, in milliseconds.</param>
    public static SqliteConnection CreateForStoreKind(
        string connectionString,
        string storeKind,
        int busyTimeoutMs = DefaultBusyTimeoutMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKind);
        var connection = Create(connectionString, busyTimeoutMs);
        StoreKinds.Add(connection, storeKind);
        return connection;
    }

    // Keyed on the connection instance so the declared kind travels with it into the StateChange
    // handler without changing the shape of the handler's captured state, and is collected with the
    // connection rather than leaking for the process lifetime.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SqliteConnection, string> StoreKinds = new();

    /// <summary>
    /// Attaches the busy-timeout <c>StateChange</c> Open-handler to an existing connection without
    /// otherwise altering it. Exposed for stores that already own connection construction (e.g. a
    /// cached, long-lived connection) but still want the single shared timeout policy.
    /// </summary>
    /// <param name="connection">The connection to attach the handler to.</param>
    /// <param name="busyTimeoutMs">
    /// The <c>busy_timeout</c> to apply on open, in milliseconds. Defaults to
    /// <see cref="DefaultBusyTimeoutMs"/>.
    /// </param>
    public static void AttachBusyTimeout(SqliteConnection connection, int busyTimeoutMs = DefaultBusyTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (busyTimeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(busyTimeoutMs), busyTimeoutMs, "busy_timeout must be non-negative.");
        }

        connection.StateChange += BusyTimeoutOnOpen;

        void BusyTimeoutOnOpen(object? sender, System.Data.StateChangeEventArgs e)
        {
            // NB: deliberately NOT unsubscribed on close. busy_timeout is per-connection and
            // resets to 0 on every open, so the subscription must survive a close/reopen cycle
            // (pinned by Create_reapplies_busy_timeout_after_reopen). Lifetime safety comes from
            // not capturing the connection plus the handle guard below, not from detaching.
            if (e.CurrentState != System.Data.ConnectionState.Open)
            {
                return;
            }

            // Use the event's sender rather than a captured local: the delegate must not hold the
            // connection it is attached to, or a stale subscription can drive a command against a
            // connection whose native handle is already gone (#2977).
            if (sender is not SqliteConnection opened)
            {
                return;
            }

            // The managed connection can report Open while the underlying SQLitePCL.sqlite3 handle
            // has already been released underneath it (observed under parallel load in the core
            // gate). Preparing a statement against that handle throws ObjectDisposedException from
            // inside the callback and onto the caller's Open stack, so skip rather than attempt.
            if (opened.Handle is null || opened.Handle.IsInvalid || opened.Handle.IsClosed)
            {
                return;
            }

            try
            {
                using var openActivity = ActivitySource.StartActivity("sqlite.connection_open_policy");
                openActivity?.SetTag("db.system", "sqlite");

                using var pragma = opened.CreateCommand();
                pragma.CommandText = $"PRAGMA busy_timeout={busyTimeoutMs};";
                pragma.ExecuteNonQuery();

                pragma.CommandText = "PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();

                // Verify world ownership before the validation boundary creates or updates its
                // durable stamp. Opening the wrong world's database must remain read-only failure.
                StoreKinds.TryGetValue(opened, out var declaredKind);
                SqliteStoreIdentityGuard.Verify(opened, declaredKind);
                EnsureForeignKeyIntegrity(opened);
            }
            catch (ObjectDisposedException)
            {
                // Lost a race with disposal between the handle check and the prepare. busy_timeout
                // is a best-effort per-connection tuning pragma on a connection that is going away
                // regardless; it must never throw out of a StateChange callback (#2977). Any other
                // exception is a genuine fault and is deliberately left to propagate.
                return;
            }

        }
    }

    private static void EnsureForeignKeyIntegrity(SqliteConnection connection)
    {
        if (connection.DataSource is not { Length: > 0 } dataSource || dataSource == ":memory:")
        {
            ValidateForeignKeys(connection, ReadSchemaGeneration(connection));
            return;
        }

        var databasePath = Path.GetFullPath(dataSource);
        var receiptPath = databasePath + ".botnexus-fk-validation";
        var lockPath = receiptPath + ".lock";
        var processLock = IntegrityLocks.GetOrAdd(databasePath, static _ => new SemaphoreSlim(1, 1));
        processLock.Wait();
        try
        {
            using var fileLock = AcquireIntegrityLock(lockPath);
            var schemaGeneration = ReadSchemaGeneration(connection);
            if (ReadValidatedGeneration(receiptPath) == schemaGeneration)
            {
                return;
            }

            ValidateForeignKeys(connection, schemaGeneration);
            WriteValidationReceipt(receiptPath, schemaGeneration);
        }
        finally
        {
            processLock.Release();
        }
    }

    private static void ValidateForeignKeys(SqliteConnection connection, long schemaGeneration)
    {
        using var validationActivity = ActivitySource.StartActivity("sqlite.foreign_key_validation");
        validationActivity?.SetTag("db.system", "sqlite");
        validationActivity?.SetTag("db.namespace", connection.DataSource);
        validationActivity?.SetTag("sqlite.schema_generation", schemaGeneration);

        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check;";
        using var reader = check.ExecuteReader();
        if (!reader.Read())
        {
            return;
        }

        var table = reader.GetString(0);
        var rowId = reader.IsDBNull(1)
            ? "unknown"
            : reader.GetInt64(1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parent = reader.GetString(2);
        validationActivity?.SetStatus(ActivityStatusCode.Error, "foreign key violation");
        throw new InvalidOperationException(
            $"SQLite store '{connection.DataSource}' contains a foreign key violation in table " +
            $"'{table}' at row {rowId} referencing '{parent}'. Remediate the orphaned row before " +
            "foreign-key enforcement can be enabled safely.");
    }

    private static long ReadSchemaGeneration(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA schema_version;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static FileStream AcquireIntegrityLock(string lockPath)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (deadline.ElapsedMilliseconds < DefaultBusyTimeoutMs)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static long? ReadValidatedGeneration(string receiptPath)
    {
        try
        {
            return long.TryParse(
                File.ReadAllText(receiptPath),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var generation)
                ? generation
                : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void WriteValidationReceipt(string receiptPath, long schemaGeneration)
    {
        var temporaryPath = receiptPath + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                schemaGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(temporaryPath, receiptPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
