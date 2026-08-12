using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Stamps and verifies the world identity of every BotNexus SQLite store (#2833).
/// </summary>
/// <remarks>
/// <para><b>Why this hangs off the connection seam.</b> #1541 made
/// <see cref="SqliteConnectionFactory"/> the single answer to "how is a BotNexus SQLite connection
/// opened". Putting the identity check anywhere else means adding it to the twelve-plus stores that
/// open connections today, and forgetting it on the thirteenth. Because the guard runs from the
/// factory's Open handler, a store type added tomorrow with no identity code of its own is still
/// verified.</para>
/// <para><b>Why it fails closed.</b> The motivating incident (#2819) resolved a path to the wrong
/// home and wrote 177 phantom cron jobs plus 1,474 poisoned run rows into live production state.
/// Nothing threw, because opening a SQLite file by path always succeeds. A store that can state
/// which world it belongs to turns that silent corruption into a startup failure.</para>
/// </remarks>
public static class SqliteStoreIdentityGuard
{
    private static readonly ConcurrentDictionary<string, byte> VerifiedPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, byte> AdoptionWarned =
        new(StringComparer.OrdinalIgnoreCase);

    private static SqliteStoreIdentity? _identity;
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// The identity this process asserts, or <see langword="null"/> when none has been configured.
    /// </summary>
    /// <remarks>
    /// When null the guard is inert: tests, tools and any host that has not opted in keep working
    /// exactly as before. Verification is only meaningful once the process can say which world it is.
    /// </remarks>
    public static SqliteStoreIdentity? Identity => _identity;

    /// <summary>
    /// Installs the process-wide store identity. Called once from composition root with the single
    /// already-resolved world ID; consumers never re-derive it.
    /// </summary>
    public static void Configure(SqliteStoreIdentity identity, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Attaches a real logger after <see cref="Configure"/> has already run, without disturbing the
    /// identity or the per-path memo.
    /// </summary>
    /// <remarks>
    /// The identity must be installed at DI-registration time, before any store can open a
    /// connection, but the logging pipeline does not exist yet at that point. Re-calling
    /// <see cref="Configure"/> later would be wrong: it would leave the memo intact but imply a
    /// re-resolution of the identity, which is exactly the second derivation this design forbids.
    /// </remarks>
    public static void SetLogger(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Removes the configured identity and clears the per-path memo. Test seam; also the way a host
    /// tears an isolated world down without leaking its identity into the next one.
    /// </summary>
    public static void Reset()
    {
        _identity = null;
        _logger = NullLogger.Instance;
        VerifiedPaths.Clear();
        AdoptionWarned.Clear();
    }

    /// <summary>
    /// Derives a store kind from a database file path (<c>cron.sqlite</c> -&gt; <c>cron</c>). Used
    /// when a caller does not name its own kind, so an unmodified store still gets a meaningful
    /// stamp rather than an empty one.
    /// </summary>
    public static string DeriveStoreKind(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            return "unknown";

        var name = Path.GetFileNameWithoutExtension(databasePath);
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name.ToLowerInvariant();
    }

    /// <summary>
    /// Verifies (and, where the rules allow, stamps) the identity of the store behind an already-open
    /// connection. Idempotent per store path within a process - the memo keeps the check off the hot
    /// path for every subsequent connection to the same file.
    /// </summary>
    /// <param name="connection">An open connection to the store.</param>
    /// <param name="storeKind">
    /// The kind the caller believes it is opening, or <see langword="null"/> to derive it from the path.
    /// </param>
    /// <exception cref="SqliteStoreIdentityMismatchException">
    /// The store belongs to a different world, or holds a different kind than requested.
    /// </exception>
    public static void Verify(SqliteConnection connection, string? storeKind = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var identity = _identity;
        if (identity is null)
            return;

        var path = connection.DataSource;

        // In-memory stores have no durable identity to protect and no path that can be mis-resolved,
        // which is the entire failure mode being guarded. Stamping them would only add a table that
        // every schema-shape assertion in the codebase would then have to know about.
        if (string.IsNullOrWhiteSpace(path) || path.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
            return;

        var kind = string.IsNullOrWhiteSpace(storeKind) ? DeriveStoreKind(path) : storeKind!;
        var memoKey = path + "\u0000" + kind;
        if (VerifiedPaths.ContainsKey(memoKey))
            return;

        VerifyCore(connection, identity, kind, path);
        VerifiedPaths[memoKey] = 0;
    }

    private static void VerifyCore(
        SqliteConnection connection,
        SqliteStoreIdentity identity,
        string kind,
        string path)
    {
        var hasMeta = TableExists(connection, SqliteStoreIdentity.TableName);

        if (!hasMeta)
        {
            // Rule 1 (empty) and rule 5 (pre-existing tables) both end in a stamp; they differ only in
            // whether the operator is told. An unstamped store with data in it is an adoption - it may
            // be a store that predates this check, or it may be the wrong file, and the difference is
            // not knowable from here. Stamping it makes every subsequent open decidable.
            var adopted = HasUserTables(connection);
            StampIdentity(connection, identity, kind);

            if (adopted && AdoptionWarned.TryAdd(path, 0))
            {
                _logger.LogWarning(
                    "Adopted unstamped SQLite store '{StorePath}' into world {WorldId} (kind '{StoreKind}', home '{HomePath}'). " +
                    "The store predates world-identity stamping; if this path is not this world's data, stop the process now.",
                    path, identity.WorldId, kind, identity.HomePath);
            }

            return;
        }

        var storedWorld = ReadMeta(connection, SqliteStoreIdentity.WorldIdKey);
        var storedKind = ReadMeta(connection, SqliteStoreIdentity.StoreKindKey);

        // A store_meta table that exists but carries no world_id is still an adoption, not a mismatch:
        // there is no competing identity to disagree with.
        if (string.IsNullOrWhiteSpace(storedWorld))
        {
            StampIdentity(connection, identity, kind);
            return;
        }

        if (!string.Equals(storedWorld, identity.WorldId, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteStoreIdentityMismatchException(
                $"SQLite store '{path}' belongs to world '{storedWorld}' but this process is running as world " +
                $"'{identity.WorldId}' (home '{identity.HomePath}'). Refusing to open it: continuing would read and " +
                "write another world's data. This usually means a store path resolved to a fallback location " +
                "instead of the configured home.",
                identity.WorldId,
                storedWorld!,
                path,
                identity.HomePath);
        }

        if (!string.IsNullOrWhiteSpace(storedKind)
            && !string.Equals(storedKind, kind, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteStoreIdentityMismatchException(
                $"SQLite store '{path}' holds '{storedKind}' data but was opened as the '{kind}' store " +
                $"(world '{identity.WorldId}', home '{identity.HomePath}'). Refusing to open it: a swapped store " +
                "path corrupts both stores.",
                identity.WorldId,
                storedWorld!,
                path,
                identity.HomePath);
        }
    }

    private static void StampIdentity(SqliteConnection connection, SqliteStoreIdentity identity, string kind)
    {
        using var create = connection.CreateCommand();
        create.CommandText =
            $"CREATE TABLE IF NOT EXISTS {SqliteStoreIdentity.TableName} (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        create.ExecuteNonQuery();

        WriteMeta(connection, SqliteStoreIdentity.WorldIdKey, identity.WorldId);
        WriteMeta(connection, SqliteStoreIdentity.StoreKindKey, kind);
        WriteMeta(connection, SqliteStoreIdentity.CreatedAtKey, DateTimeOffset.UtcNow.ToString("O"));
        WriteMeta(connection, SqliteStoreIdentity.CreatedByVersionKey, ResolveVersion());
    }

    private static string ResolveVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private static void WriteMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {SqliteStoreIdentity.TableName} (key, value) VALUES ($key, $value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {SqliteStoreIdentity.TableName} WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool HasUserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    internal static bool IsOpen(SqliteConnection connection) => connection.State == ConnectionState.Open;
}
