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

        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState == System.Data.ConnectionState.Open)
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = $"PRAGMA busy_timeout={busyTimeoutMs};";
                pragma.ExecuteNonQuery();
            }
        };
    }
}
