using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

/// <summary>Describes whether a legacy extension migration was safe and whether it changed the document.</summary>
public sealed record LegacyGatewayExtensionsMigrationResult(bool Applied, IReadOnlyList<string> Errors)
{
    /// <summary>Indicates that the migration can be used without losing or overwriting configuration.</summary>
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// Migrates the retired <c>gateway.extensions</c> loader/defaults shape into its canonical homes.
/// </summary>
public static class LegacyGatewayExtensionsMigration
{
    /// <summary>Returns whether the document contains any legacy extension settings.</summary>
    public static bool IsApplicable(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TryGetLegacyExtensions(document.Root, out _);
    }

    /// <summary>
    /// Copies legacy values to canonical paths and removes only values that were safely consumed.
    /// Existing canonical values always win. An incompatible canonical parent or malformed legacy
    /// value rejects the whole migration and leaves the input byte-for-byte equivalent.
    /// </summary>
    public static LegacyGatewayExtensionsMigrationResult Apply(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!TryGetLegacyExtensions(document.Root, out _))
            return new(false, []);

        var candidate = document.Root.DeepClone().AsObject();
        var errors = ApplyCore(candidate);
        if (errors.Count > 0)
            return new(false, errors);

        document.ReplaceWith(new ConfigDocument(candidate));
        return new(true, []);
    }

    private static IReadOnlyList<string> ApplyCore(JsonObject root)
    {
        _ = TryGetLegacyExtensions(root, out var legacy);
        var errors = ValidateLegacyShapes(legacy);
        if (errors.Count > 0)
            return errors;

        var gateway = Find(root, "gateway")!.Value.Value!.AsObject();
        var hasLoaderValues = Find(legacy, "path") is not null || Find(legacy, "enabled") is not null;
        JsonObject? loader = null;
        if (hasLoaderValues && !TryGetOrCreateObject(gateway, "extensionLoader", "gateway.extensionLoader", out loader, out var loaderError))
            errors.Add(loaderError!);

        var legacyDefaultsEntry = Find(legacy, "defaults");
        JsonObject? canonicalExtensions = null;
        if (legacyDefaultsEntry is not null)
        {
            if (!TryGetOrCreateObject(root, "agents", "agents", out var agents, out var agentsError))
                errors.Add(agentsError!);
            else if (!TryGetOrCreateObject(agents, "defaults", "agents.defaults", out var defaults, out var defaultsError))
                errors.Add(defaultsError!);
            else if (!TryGetOrCreateObject(defaults, "extensions", "agents.defaults.extensions", out canonicalExtensions, out var extensionsError))
                errors.Add(extensionsError!);
        }

        if (errors.Count > 0)
            return errors;

        if (loader is not null)
        {
            CopyWhenAbsent(legacy, "path", loader);
            CopyWhenAbsent(legacy, "enabled", loader);
        }

        if (legacyDefaultsEntry?.Value is JsonObject legacyDefaults && canonicalExtensions is not null)
        {
            foreach (var entry in legacyDefaults)
            {
                if (Find(canonicalExtensions, entry.Key) is null)
                    canonicalExtensions[entry.Key] = entry.Value?.DeepClone();
            }
        }

        Remove(legacy, "path");
        Remove(legacy, "enabled");
        Remove(legacy, "defaults");
        if (legacy.Count == 0)
            Remove(gateway, "extensions");
        return [];
    }

    private static List<string> ValidateLegacyShapes(JsonObject legacy)
    {
        var errors = new List<string>();
        ValidateShape(legacy, "defaults", node => node is JsonObject, "object", errors);
        ValidateShape(legacy, "path", IsStringOrNull, "string or null", errors);
        ValidateShape(legacy, "enabled", IsBooleanOrNull, "boolean or null", errors);
        return errors;
    }

    private static void ValidateShape(
        JsonObject parent,
        string name,
        Func<JsonNode?, bool> predicate,
        string expected,
        ICollection<string> errors)
    {
        var entry = Find(parent, name);
        if (entry is not null && !predicate(entry.Value.Value))
            errors.Add($"gateway.extensions.{name} must be {expected}; the legacy value was not migrated or removed.");
    }

    private static bool IsStringOrNull(JsonNode? node)
        => node is null || node is JsonValue value && value.TryGetValue<string>(out _);

    private static bool IsBooleanOrNull(JsonNode? node)
        => node is null || node is JsonValue value && value.TryGetValue<bool>(out _);

    private static bool TryGetLegacyExtensions(JsonObject root, out JsonObject extensions)
    {
        extensions = null!;
        if (Find(root, "gateway")?.Value is not JsonObject gateway
            || Find(gateway, "extensions")?.Value is not JsonObject candidate)
            return false;

        if (Find(candidate, "path") is null
            && Find(candidate, "enabled") is null
            && Find(candidate, "defaults") is null)
            return false;

        extensions = candidate;
        return true;
    }

    private static void CopyWhenAbsent(JsonObject source, string name, JsonObject destination)
    {
        var sourceEntry = Find(source, name);
        if (sourceEntry is not null && Find(destination, name) is null)
            destination[name] = sourceEntry.Value.Value?.DeepClone();
    }

    private static bool TryGetOrCreateObject(
        JsonObject parent,
        string name,
        string canonicalPath,
        out JsonObject result,
        out string? error)
    {
        var entry = Find(parent, name);
        if (entry is null)
        {
            result = new JsonObject();
            parent[name] = result;
            error = null;
            return true;
        }

        if (entry.Value.Value is JsonObject existing)
        {
            result = existing;
            error = null;
            return true;
        }

        result = null!;
        error = $"Cannot migrate legacy extension settings because canonical '{name}' at "
            + $"'{canonicalPath}' is explicitly null or is not an object. "
            + "The canonical value was preserved and the conflicting legacy value was not removed.";
        return false;
    }

    private static KeyValuePair<string, JsonNode?>? Find(JsonObject parent, string name)
    {
        foreach (var pair in parent)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                return pair;
        }
        return null;
    }

    private static void Remove(JsonObject parent, string name)
    {
        var entry = Find(parent, name);
        if (entry is not null)
            parent.Remove(entry.Value.Key);
    }
}
