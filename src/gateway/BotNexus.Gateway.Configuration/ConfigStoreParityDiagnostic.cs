using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Configuration;

/// <summary>Availability of an independently inspected backend, never an effective runtime fallback.</summary>
public enum ConfigParityBackendState
{
    /// <summary>No backend was found.</summary>
    Absent,
    /// <summary>The complete document was read successfully.</summary>
    Readable,
    /// <summary>The backend could not safely supply a complete document.</summary>
    Unreadable
}

/// <summary>Outcome of an explicit, read-only compatibility comparison.</summary>
public enum ConfigStoreParityStatus
{
    /// <summary>Both complete documents were read and are equivalent.</summary>
    Clean,
    /// <summary>Both documents were read and differ.</summary>
    Drift,
    /// <summary>Only the compatibility document exists; SQL is not enabled.</summary>
    Disabled,
    /// <summary>Neither backend exists.</summary>
    Absent,
    /// <summary>SQL exists without a compatibility document; no comparison was possible.</summary>
    SqlOnly,
    /// <summary>At least one backend could not be inspected safely.</summary>
    Unreadable
}

/// <summary>
/// A bounded diagnostic containing no configuration paths, keys, values, hashes, or exception data.
/// Counts are populated only when both backends can be compared. A missing subtree counts once;
/// arrays are compared as whole values, matching configuration replacement semantics.
/// </summary>
/// <param name="Status">Comparison outcome, distinct from backend availability.</param>
/// <param name="SqlBackend">SQL availability.</param>
/// <param name="CompatibilityBackend">Compatibility document availability.</param>
/// <param name="SqlOnlyCount">Properties/subtrees present only in SQL.</param>
/// <param name="CompatibilityOnlyCount">Properties/subtrees present only in compatibility data.</param>
/// <param name="ChangedCount">Shared properties with different values or container types.</param>
public sealed record ConfigStoreParityReport(
    ConfigStoreParityStatus Status,
    ConfigParityBackendState SqlBackend,
    ConfigParityBackendState CompatibilityBackend,
    int SqlOnlyCount = 0,
    int CompatibilityOnlyCount = 0,
    int ChangedCount = 0);

/// <summary>
/// Explicit operator diagnostic, not a runtime configuration source. Inspects compatibility data
/// independently of SQL so effective-provider precedence cannot conceal drift. Never initializes,
/// migrates, reconciles, narrows permissions, or writes configuration.
/// </summary>
public static class ConfigStoreParityDiagnostic
{
    /// <summary>
    /// Compares the complete stored document with the compatibility document at the supplied path.
    /// SQL is opened read-only and nonpooled in one deferred read transaction, honoring committed WAL.
    /// SQLite may create/update WAL/SHM coordination files; configuration values, schema, revision,
    /// permissions and migration state are never changed. Legacy filenames are
    /// discovered without invoking the migration-capable path resolver; ambiguous names fail closed.
    /// Each backend is read independently; the pair is not an atomic cross-backend snapshot.
    /// </summary>
    public static async Task<ConfigStoreParityReport> InspectAsync(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        cancellationToken.ThrowIfCancellationRequested();

        var (sqlState, sql) = await ReadSqlAsync(configPath, cancellationToken).ConfigureAwait(false);
        var (compatibilityState, compatibility) = await ReadCompatibilityAsync(configPath, cancellationToken).ConfigureAwait(false);

        if (sqlState == ConfigParityBackendState.Unreadable || compatibilityState == ConfigParityBackendState.Unreadable)
            return new(ConfigStoreParityStatus.Unreadable, sqlState, compatibilityState);
        if (sqlState == ConfigParityBackendState.Absent)
            return new(compatibilityState == ConfigParityBackendState.Absent ? ConfigStoreParityStatus.Absent : ConfigStoreParityStatus.Disabled,
                sqlState, compatibilityState);
        if (compatibilityState == ConfigParityBackendState.Absent)
            return new(ConfigStoreParityStatus.SqlOnly, sqlState, compatibilityState);

        if (sql is null || compatibility is null)
            return new(ConfigStoreParityStatus.Unreadable, sqlState, compatibilityState);

        var counts = Compare(sql, compatibility, cancellationToken);
        return new(counts == (0, 0, 0) ? ConfigStoreParityStatus.Clean : ConfigStoreParityStatus.Drift,
            sqlState, compatibilityState, counts.SqlOnly, counts.CompatibilityOnly, counts.Changed);
    }

    /// <summary>Maximum UTF-8 bytes accepted from either complete document or aggregate SQL data.</summary>
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;
    /// <summary>Maximum SQL rows inspected; exceeding it refuses the entire comparison.</summary>
    public const int MaximumSqlRows = 10_000;
    // A home with more entries is not completely discoverable within this diagnostic budget.
    internal const int MaximumDiscoveryEntries = 10_000;
    private const int MaximumDatabaseBytes = 32 * 1024 * 1024;
    private const int MaximumPathCharacters = 1024;
    private const int MaximumDepth = 32;
    private const int MaximumNodes = 100_000;

    internal static async Task<(ConfigParityBackendState State, JsonObject? Document)> ReadCompatibilityAsync(
        string configPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = await ReadBoundedAsync(stream, MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
            var budget = MaximumNodes;
            var document = ParseComplete(bytes, ref budget, cancellationToken);
            return document is JsonObject root
                ? (ConfigParityBackendState.Readable, root)
                : (ConfigParityBackendState.Unreadable, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (ConfigParityBackendState.Absent, null);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return (ConfigParityBackendState.Unreadable, null);
        }
    }

    internal static async Task<(ConfigParityBackendState State, JsonObject? Document)> ReadSqlAsync(
        string configPath, CancellationToken cancellationToken, bool requireCurrentCanonicalStore = false)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (directory is null)
                return (ConfigParityBackendState.Unreadable, null);
            string[] candidates;
            try
            {
                var discovered = DiscoverStoreCandidates(Directory.EnumerateFiles(directory), cancellationToken);
                if (discovered is null)
                    return (ConfigParityBackendState.Unreadable, null);
                candidates = discovered;
            }
            catch (DirectoryNotFoundException)
            {
                return (ConfigParityBackendState.Absent, null);
            }
            if (candidates.Length == 0)
                return (ConfigParityBackendState.Absent, null);
            if (candidates.Length != 1 || (requireCurrentCanonicalStore && !string.Equals(Path.GetFileName(candidates[0]),
                    ConfigStoreBootstrap.StoreFileName, StringComparison.Ordinal)))
                return (ConfigParityBackendState.Unreadable, null);

            // Native read-only SQLite reads the coherent committed view, including WAL. It may
            // coordinate WAL/SHM sidecars, but cannot mutate configuration rows/schema/revision.
            // Never use immutable/main-only reads: those can silently omit committed WAL frames.
            if (new FileInfo(candidates[0]).Length > MaximumDatabaseBytes)
                throw new InvalidOperationException("Database inspection byte limit exceeded.");
            RefuseRecoveryJournal(candidates[0]);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = candidates[0],
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            // Deferred BEGIN avoids requesting a writer reservation on a read-only connection.
            await using var transaction = connection.BeginTransaction(deferred: true);
            return await ReadSnapshotEntriesAsync(connection, transaction, cancellationToken, requireCurrentCanonicalStore).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return (ConfigParityBackendState.Unreadable, null);
        }
    }

    // Null means the complete discovery budget was exceeded, never a partial successful scan.
    // IEnumerable keeps cancellation/budget tests deterministic without enormous disk fixtures.
    internal static string[]? DiscoverStoreCandidates(IEnumerable<string> paths, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var legacyName = Path.ChangeExtension(ConfigStoreBootstrap.StoreFileName, SqliteStorePathPolicy.LegacyExtension);
        var candidates = new List<string>(2);
        var scanned = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (++scanned > MaximumDiscoveryEntries)
                return null;
            var name = Path.GetFileName(path);
            if (string.Equals(name, ConfigStoreBootstrap.StoreFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, legacyName, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(path);
                if (candidates.Count == 2)
                    return candidates.ToArray(); // Already ambiguous: no need to scan further.
            }
        }
        ct.ThrowIfCancellationRequested();
        return candidates.ToArray();
    }

    private static void RefuseRecoveryJournal(string path)
    {
        // Recovery is not a diagnostic's job. Do not initialize, recover or delete a journal.
        try
        {
            _ = File.GetAttributes(path + "-journal");
        }
        catch (FileNotFoundException)
        {
            return;
        }
        throw new InvalidOperationException("Store requires recovery inspection.");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken ct)
    {
        if (stream.Length > maximumBytes)
            throw new InvalidOperationException("Inspection byte limit exceeded.");
        using var sink = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0)
                break;
            if (sink.Length + count > maximumBytes)
                throw new InvalidOperationException("Inspection byte limit exceeded.");
            sink.Write(buffer, 0, count);
        }
        return sink.ToArray();
    }

    private static async Task<(ConfigParityBackendState, JsonObject?)> ReadSnapshotEntriesAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct, bool strict)
    {
        await using var metadata = connection.CreateCommand();
        metadata.Transaction = transaction;
        metadata.CommandText = "PRAGMA user_version;";
        var pragma = Convert.ToInt32(await metadata.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        metadata.CommandText = $"SELECT 1 FROM store_meta WHERE length(CAST(key AS BLOB)) > {MaximumPathCharacters * 4} OR length(CAST(value AS BLOB)) > {MaximumPathCharacters * 4} LIMIT 1;";
        if (await metadata.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Metadata byte limit exceeded.");
        metadata.CommandText = "SELECT key, value FROM store_meta LIMIT 101;";
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await metadata.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (meta.Count >= 100 || reader.GetString(0).Length > MaximumPathCharacters
                    || reader.GetString(1).Length > MaximumPathCharacters || !meta.TryAdd(reader.GetString(0), reader.GetString(1)))
                    throw new InvalidOperationException("Unsupported metadata.");
            }
        }
        if (!meta.TryGetValue(SqliteSchemaVersion.SchemaVersionKey, out var rawVersion)
            || !int.TryParse(rawVersion, out var version) || version < 1
            || version > SqliteConfigStore.CurrentSchemaVersion || pragma != version
            || (strict && version != SqliteConfigStore.CurrentSchemaVersion))
            throw new InvalidOperationException("Unsupported schema.");
        var identity = SqliteStoreIdentityGuard.Identity;
        meta.TryGetValue(SqliteStoreIdentity.WorldIdKey, out var world);
        meta.TryGetValue(SqliteStoreIdentity.StoreKindKey, out var kind);
        if ((!string.IsNullOrWhiteSpace(kind) && !string.Equals(kind, "config", StringComparison.OrdinalIgnoreCase))
            || (identity is not null && (string.IsNullOrWhiteSpace(world) || !string.Equals(world, identity.WorldId, StringComparison.OrdinalIgnoreCase)))
            || (identity is null && !string.IsNullOrWhiteSpace(world)))
            throw new InvalidOperationException("Store identity cannot be verified without adoption.");
        if (strict)
        {
            metadata.CommandText = "SELECT revision FROM config_revision WHERE singleton = 1;";
            if (await metadata.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long revision || revision < 0)
                throw new InvalidOperationException("Unsupported revision.");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Refuse oversized cells before materializing their text into managed memory.
        command.CommandText = $"""
            SELECT scope, scope_id, key_path, state, value FROM config_entries
            WHERE length(CAST(key_path AS BLOB)) > {MaximumPathCharacters * 4}
               OR length(CAST(value AS BLOB)) > {MaximumDocumentBytes}
               OR length(CAST(scope_id AS BLOB)) > {MaximumPathCharacters} LIMIT 1;
            """;
        await using (var oversized = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            if (await oversized.ReadAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("SQL cell limit exceeded.");
        command.CommandText = $"SELECT scope, scope_id, key_path, state, value FROM config_entries LIMIT {MaximumSqlRows + 1};";
        var entries = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal);
        var aggregateBytes = 0;
        var budget = MaximumNodes;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var path = reader.GetString(2);
                var state = (ConfigValueState)reader.GetInt32(3);
                var value = reader.IsDBNull(4) ? null : reader.GetString(4);
                aggregateBytes += System.Text.Encoding.UTF8.GetByteCount(path) + System.Text.Encoding.UTF8.GetByteCount(value ?? "");
                if (reader.GetInt32(0) != (int)ConfigScope.World || reader.GetString(1).Length != 0
                    || entries.Count >= MaximumSqlRows || aggregateBytes > MaximumDocumentBytes
                    || path.Length == 0 || path.Length > MaximumPathCharacters || path.Split('.').Length > MaximumDepth
                    || path.Split('.').Any(string.IsNullOrEmpty)
                    || state is not (ConfigValueState.Value or ConfigValueState.ExplicitNull)
                    || !entries.TryAdd(path, new(path, state, value)))
                    throw new InvalidOperationException("Unsupported stored entry.");
                if (state == ConfigValueState.Value)
                {
                    if (value is null || ParseComplete(System.Text.Encoding.UTF8.GetBytes(value), ref budget, ct) is null)
                        throw new InvalidOperationException("Invalid stored JSON.");
                }
                else if (value is not null)
                    throw new InvalidOperationException("Invalid null state.");
            }
        }
        foreach (var path in entries.Keys)
        {
            ct.ThrowIfCancellationRequested();
            for (var index = path.IndexOf('.'); index >= 0; index = path.IndexOf('.', index + 1))
                if (entries.ContainsKey(path[..index]))
                    throw new InvalidOperationException("Conflicting stored shapes.");
        }
        var document = ConfigDocumentRehydrator.Rehydrate(entries);
        budget = MaximumNodes;
        ValidateComplete(document, 0, ref budget, ct);
        if (strict)
        {
            var flattened = ConfigDocumentFlattener.Flatten(document);
            if (entries.Count != flattened.Count || entries.Any(pair =>
                    !flattened.TryGetValue(pair.Key, out var entry) || entry.State != pair.Value.State
                    || !JsonNode.DeepEquals(JsonNode.Parse(entry.Value ?? "null"), JsonNode.Parse(pair.Value.Value ?? "null"))))
                throw new InvalidOperationException("Lossy stored document.");
        }
        return (ConfigParityBackendState.Readable, document);
    }

    // A union of two individually eligible sources must itself remain readable after persistence.
    // Use the reader's exact JSON/path/row budgets, including the JSON writer's indented encoding.
    internal static bool IsEligibleReconciliationOutput(JsonObject document, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaximumDocumentBytes)
            return false;
        var budget = MaximumNodes;
        _ = ParseComplete(bytes, ref budget, ct);
        var entries = ConfigDocumentFlattener.Flatten(document);
        if (entries.Count > MaximumSqlRows)
            return false;
        var aggregateBytes = 0;
        budget = MaximumNodes;
        foreach (var (path, entry) in entries)
        {
            ct.ThrowIfCancellationRequested();
            aggregateBytes += System.Text.Encoding.UTF8.GetByteCount(path) + System.Text.Encoding.UTF8.GetByteCount(entry.Value ?? "");
            if (aggregateBytes > MaximumDocumentBytes || path.Length == 0 || path.Length > MaximumPathCharacters
                || path.Split('.').Length > MaximumDepth || path.Split('.').Any(string.IsNullOrEmpty))
                return false;
            if (entry.Value is { } value)
                _ = ParseComplete(System.Text.Encoding.UTF8.GetBytes(value), ref budget, ct);
        }
        return true;
    }

    private static JsonNode? ParseComplete(byte[] bytes, ref int budget, CancellationToken ct)
    {
        var document = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = MaximumDepth });
        ValidateComplete(document, 0, ref budget, ct);
        return document;
    }

    private static void ValidateComplete(JsonNode? node, int depth, ref int budget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (--budget < 0 || depth > MaximumDepth)
            throw new InvalidOperationException("Document complexity limit exceeded.");
        if (node is JsonObject obj)
        {
            // Enumeration forces lazy duplicate-key detection even in unmatched subtrees.
            foreach (var pair in obj)
            {
                if (pair.Key.Length > MaximumPathCharacters)
                    throw new InvalidOperationException("Document key limit exceeded.");
                ValidateComplete(pair.Value, depth + 1, ref budget, ct);
            }
        }
        else if (node is JsonArray array)
            foreach (var item in array)
                ValidateComplete(item, depth + 1, ref budget, ct);
    }

    private static bool IsReadFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SqliteException or JsonException or
        InvalidOperationException or ArgumentException or NotSupportedException or FormatException or OverflowException or InvalidCastException;

    private static bool EqualComplete(JsonNode? left, JsonNode? right, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (left is JsonObject leftObject && right is JsonObject rightObject)
            return leftObject.Count == rightObject.Count && leftObject.All(pair =>
                rightObject.TryGetPropertyValue(pair.Key, out var other) && EqualComplete(pair.Value, other, ct));
        if (left is JsonArray leftArray && right is JsonArray rightArray)
            return leftArray.Count == rightArray.Count && leftArray.Zip(rightArray).All(pair => EqualComplete(pair.First, pair.Second, ct));
        return JsonNode.DeepEquals(left, right);
    }

    private static (int SqlOnly, int CompatibilityOnly, int Changed) Compare(JsonObject sql, JsonObject compatibility, CancellationToken ct)
    {
        var sqlOnly = 0;
        var compatibilityOnly = 0;
        var changed = 0;
        foreach (var (key, value) in sql)
        {
            ct.ThrowIfCancellationRequested();
            if (!compatibility.TryGetPropertyValue(key, out var other))
                sqlOnly++;
            else if (value is JsonObject sqlObject && other is JsonObject compatibilityObject)
            {
                var nested = Compare(sqlObject, compatibilityObject, ct);
                sqlOnly += nested.SqlOnly;
                compatibilityOnly += nested.CompatibilityOnly;
                changed += nested.Changed;
            }
            else if (!EqualComplete(value, other, ct))
                changed++;
        }
        foreach (var key in compatibility.Select(pair => pair.Key))
        {
            ct.ThrowIfCancellationRequested();
            if (!sql.ContainsKey(key))
                compatibilityOnly++;
        }
        return (sqlOnly, compatibilityOnly, changed);
    }
}
