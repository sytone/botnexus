using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// An immutable, process-local preview. Configuration and opaque concurrency tokens stay internal;
/// public diagnostics never expose keys, values, paths or secret-derived fingerprints.
/// </summary>
public sealed class ConfigReconciliationPreview
{
    /// <summary>Whether the complete candidate passed safety and configuration validation.</summary>
    public bool CanApply { get; }
    /// <summary>A bounded, secret-free verdict suitable for operator output.</summary>
    public string Outcome { get; }
    internal string ConfigPath { get; }
    internal JsonObject? Sql { get; }
    internal JsonObject? Compatibility { get; }
    internal JsonObject? Candidate { get; }
    internal string? SqlRevision { get; }
    internal string? CompatibilityRevision { get; }

    internal ConfigReconciliationPreview(string configPath, string outcome, JsonObject? sql = null,
        JsonObject? compatibility = null, JsonObject? candidate = null,
        string? sqlRevision = null, string? compatibilityRevision = null)
    {
        ConfigPath = configPath;
        Outcome = outcome;
        Sql = sql;
        Compatibility = compatibility;
        Candidate = candidate;
        SqlRevision = sqlRevision;
        CompatibilityRevision = compatibilityRevision;
        CanApply = candidate is not null;
    }
}

public sealed partial class PlatformConfigWriter
{
    /// <summary>
    /// Independently reads existing SQL and compatibility documents without initializing a store,
    /// taking a writer lock, creating backups, or migrating a legacy filename. SQLite may update
    /// WAL/SHM coordination files, but preview never changes configuration values, schema, revision,
    /// permissions or migration state. SQL wins shared leaf
    /// conflicts; compatibility-only fields and explicit nulls survive. Unsafe shapes fail closed.
    /// </summary>
    public static async Task<ConfigReconciliationPreview> PreviewReconciliationAsync(
        string configPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var path = Path.GetFullPath(configPath);
        var (sqlState, sql) = await ConfigStoreParityDiagnostic.ReadSqlAsync(path, ct, requireCurrentCanonicalStore: true);
        var (compatibilityState, compatibility) = await ConfigStoreParityDiagnostic.ReadCompatibilityAsync(path, ct);
        if (sqlState != ConfigParityBackendState.Readable || sql is null
            || compatibilityState == ConfigParityBackendState.Unreadable)
            return new(path, "Reconciliation refused: a current canonical SQL store and readable or absent compatibility document are required.");
        try
        {
            if (!IsLossless(sql) || (compatibility is not null && !IsLossless(compatibility)))
                return new(path, "Reconciliation refused: ambiguous keys or a lossy document representation.");
            var candidate = sql.DeepClone().AsObject();
            if (compatibility is not null && !MergeUnion(candidate, compatibility))
                return new(path, "Reconciliation refused: incompatible object, leaf or array shapes.");
            if (!IsLossless(candidate) || !ConfigStoreParityDiagnostic.IsEligibleReconciliationOutput(candidate, ct))
                return new(path, "Reconciliation refused: the union cannot round-trip losslessly.");
            if (ValidateMutation(sql, candidate, true, null, null).Count > 0
                || (compatibility is not null && ValidateMutation(compatibility, candidate, true, null, null).Count > 0))
                return new(path, "Reconciliation refused: complete candidate validation or a destructive-section guard failed.");
            return new(path, "Reconciliation preview is safe; no configuration was changed.", sql, compatibility,
                candidate, ComputeRevision(sql), compatibility is null ? null : ComputeRevision(compatibility));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new(path, "Reconciliation refused: the documents cannot be represented safely.");
        }
    }

    /// <summary>
    /// Explicitly persists a previewed complete union through the canonical guarded mutation pipeline
    /// and registered backend writers. Both before-images are independently re-read under the normal
    /// lock ordering; stale previews fail before writes. Backend I/O failures propagate and may leave
    /// partial persistence: there is no cross-store atomicity guarantee. This is not an ordinary edit.
    /// </summary>
    public Task<IReadOnlyList<string>> ApplyReconciliationAsync(
        ConfigReconciliationPreview preview, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.CanApply || !SameReconciliationPath(_configPath, preview.ConfigPath))
            return Task.FromResult<IReadOnlyList<string>>(["Reconciliation refused: preview is unsafe or belongs to another configuration."]);
        var storePath = Path.Combine(Path.GetDirectoryName(preview.ConfigPath) ?? string.Empty, ConfigStoreBootstrap.StoreFileName);
        if (Backends.Count != 2 || !Backends.Any(backend => backend.Name == "sqlite" && backend.WinsOnRead
                && SameReconciliationPath(backend.Location, storePath))
            || !Backends.Any(backend => backend.Name == "json" && SameReconciliationPath(backend.Location, preview.ConfigPath)))
            return Task.FromResult<IReadOnlyList<string>>(["Reconciliation refused: canonical SQL and compatibility writers are required."]);
        return MutateCoreAsync(_ => Task.FromResult<string?>(null), "before-store-reconciliation",
            validateCandidate: true, namedSections: null, ct, reconciliation: preview);
    }

    private static bool SameReconciliationPath(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsLossless(JsonObject document)
    {
        // Case aliases are ambiguous to the platform's case-insensitive binder. Inspect containers
        // inside arrays too, even though arrays are opaque leaves to the SQL flattener.
        if (!HasUnambiguousKeys(document))
            return false;
        return JsonNode.DeepEquals(document, ConfigDocumentRehydrator.Rehydrate(ConfigDocumentFlattener.Flatten(document)));
    }

    private static bool HasUnambiguousKeys(JsonNode? node, string path = "")
    {
        if (node is JsonObject obj)
        {
            // Only the canonical capacity-map path has opaque ordinal model IDs. Fields inside
            // each capacity value still bind normally and must reject case-insensitive aliases.
            var exactIds = ExactModelCapacityConfiguration.IsMapPath(path);
            var keys = new HashSet<string>(exactIds ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var pair in obj)
            {
                var childPath = exactIds ? string.Empty : path.Length == 0 ? pair.Key : path + "." + pair.Key;
                if (pair.Key.Length == 0 || !keys.Add(pair.Key) || !HasUnambiguousKeys(pair.Value, childPath))
                    return false;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                if (!HasUnambiguousKeys(item))
                    return false;
        }
        return true;
    }

    private static bool MergeUnion(JsonObject sql, JsonObject compatibility)
    {
        foreach (var pair in compatibility)
        {
            if (!sql.TryGetPropertyValue(pair.Key, out var value))
            {
                sql[pair.Key] = pair.Value?.DeepClone();
                continue;
            }
            if (value is JsonObject sqlObject && pair.Value is JsonObject compatibilityObject)
            {
                if (!MergeUnion(sqlObject, compatibilityObject))
                    return false;
            }
            else if (!CompatibleShape(value, pair.Value))
                return false;
        }
        return true;
    }

    private static bool CompatibleShape(JsonNode? sql, JsonNode? compatibility)
    {
        if (sql is JsonObject sqlObject && compatibility is JsonObject compatibilityObject)
        {
            // Inside an opaque array leaf no union occurs; dropping compatibility-only object keys
            // would be silent loss. Require identical object structure, while SQL still wins leaves.
            return sqlObject.Count == compatibilityObject.Count && sqlObject.All(pair =>
                compatibilityObject.TryGetPropertyValue(pair.Key, out var other) && CompatibleShape(pair.Value, other));
        }
        if (sql is JsonObject || compatibility is JsonObject)
            return false;
        if (sql is JsonArray sqlArray && compatibility is JsonArray compatibilityArray)
            return sqlArray.Count == compatibilityArray.Count && sqlArray.Zip(compatibilityArray).All(pair => CompatibleShape(pair.First, pair.Second));
        return sql is not JsonArray && compatibility is not JsonArray;
    }
}
