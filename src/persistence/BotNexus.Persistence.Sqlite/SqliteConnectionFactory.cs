using Microsoft.Data.Sqlite;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Single source of truth for "how a BotNexus SQLite connection is opened" (#1541).
/// Every SQLite-backed store previously duplicated an identical <c>StateChange</c> Open-handler
/// that applied <c>PRAGMA busy_timeout=5000</c> on every fresh connection; that boilerplate
/// (and the magic <c>5000</c>) is consolidated here so the timeout value and connection-level
/// pragma policy live in exactly one place.
/// </summary>
/// <remarks>
/// <c>busy_timeout</c> is a <b>per-connection</b> setting that resets to <c>0</c> on every open,
/// so it must be re-applied on every fresh connection rather than once at database init (unlike
/// the database-level <c>journal_mode</c>, which <see cref="SqliteWalMaintenance"/> owns). The
/// factory attaches a <see cref="System.Data.Common.DbConnection.StateChange"/> handler that
/// re-applies the pragma whenever the connection transitions to
/// <see cref="System.Data.ConnectionState.Open"/>, which also covers connections that are
/// closed and reopened. Journal-mode / WAL policy remains the concern of
/// <see cref="SqliteWalMaintenance"/>; a store applies that once against an open connection after
/// obtaining it from this factory.
/// </remarks>
public static class SqliteConnectionFactory
{
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
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="storeKind">The kind the caller believes it is opening (<c>cron</c>, <c>sessions</c>, ...).</param>
    /// <param name="busyTimeoutMs">The <c>busy_timeout</c> to apply on open, in milliseconds.</param>
    public static SqliteConnection Create(
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
                using var pragma = opened.CreateCommand();
                pragma.CommandText = $"PRAGMA busy_timeout={busyTimeoutMs};";
                pragma.ExecuteNonQuery();
            }
            catch (ObjectDisposedException)
            {
                // Lost a race with disposal between the handle check and the prepare. busy_timeout
                // is a best-effort per-connection tuning pragma on a connection that is going away
                // regardless; it must never throw out of a StateChange callback (#2977). Any other
                // exception is a genuine fault and is deliberately left to propagate.
                return;
            }

            // #2833: world-identity verification runs HERE, on the single connection seam, rather
            // than in each store. That is what makes clause 5 true - a store type added tomorrow
            // with no identity code of its own is still verified, because it cannot open a
            // connection without going through this handler. A mismatch deliberately throws out of
            // the StateChange callback and onto the caller's Open stack: failing the open is the
            // whole point, and is strictly better than the alternative of silently reading and
            // writing another world's production data (#2819).
            StoreKinds.TryGetValue(opened, out var declaredKind);
            SqliteStoreIdentityGuard.Verify(opened, declaredKind);
        }
    }
}
