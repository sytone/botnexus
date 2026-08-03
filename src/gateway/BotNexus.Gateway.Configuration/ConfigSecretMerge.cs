using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Symmetric redaction / restoration of secret fields in the platform config
/// document, plus a lossless deep-merge used on the save path.
///
/// The GET path replaces every secret with <see cref="Placeholder"/> so the UI
/// never sees real secrets. When the UI later PUTs a section back, it round-trips
/// those placeholders verbatim. Writing them would clobber the real on-disk
/// secret (#1955). This helper restores the existing on-disk value anywhere the
/// incoming value is still the placeholder, keeping redaction and restoration
/// symmetric (they walk the exact same field paths).
///
/// The deep-merge keeps existing keys that the incoming payload omits so that a
/// partial/typed save never drops channel subtrees such as telegram bots or
/// serviceBus queues (#1954).
///
/// The set of secret field paths is <b>discovered by reflection</b> over the typed
/// <see cref="PlatformConfig"/> graph (#2012): every property annotated with
/// <c>[ConfigField(Secret = true)]</c> (or <c>Widget == ConfigFieldWidget.Secret</c>)
/// contributes a path, including nested POCOs and dictionary-valued sections
/// (providers, gateway.apiKeys, gateway.locations, gateway.satellites,
/// gateway.crossWorld.peers/inbound). Adding a new <c>[ConfigField(Secret = true)]</c>
/// anywhere in the graph makes it redacted+restored with no change to this class.
/// </summary>
public static class ConfigSecretMerge
{
    /// <summary>The literal value written in place of every redacted secret.</summary>
    public const string Placeholder = "***";

    /// <summary>
    /// The kind of node a discovered secret path terminates at, which decides how the
    /// value is masked and restored.
    /// </summary>
    internal enum SecretTerminal
    {
        /// <summary>A single scalar string value (e.g. <c>apiKey</c>) is the secret.</summary>
        Scalar,

        /// <summary>
        /// The terminal node is a <c>Dictionary&lt;string, string&gt;</c> whose <em>values</em>
        /// are each a secret (e.g. <c>gateway.crossWorld.inbound.apiKeys</c>).
        /// </summary>
        DictionaryValues,
    }

    /// <summary>
    /// A JSON path to a secret-bearing node. <see cref="Segments"/> is the ordered list of
    /// camelCase JSON property names to walk; a <c>"*"</c> segment matches every key of a
    /// dictionary-valued section (providers, apiKeys, locations, peers, satellites, ...).
    /// </summary>
    internal sealed record SecretPath(string[] Segments, SecretTerminal Terminal);

    // Cache of resolved string-keyed dictionary value types. Declared before PlatformSecretPaths so
    // it is initialised before the discovery walk below runs in the static initializer.
    private static readonly ConcurrentDictionary<Type, Type?> DictionaryValueTypeCache = new();

    // The discovered secret-path set is stable for the lifetime of the process (it depends only
    // on the compiled PlatformConfig type graph), so compute it once and cache it.
    private static readonly IReadOnlyList<SecretPath> PlatformSecretPaths =
        DiscoverSecretPaths(typeof(PlatformConfig));

    /// <summary>
    /// Redacts every secret-bearing field in the whole-config document in place, replacing each
    /// with <see cref="Placeholder"/>. The field set is the reflection-discovered secret paths
    /// over the <see cref="PlatformConfig"/> graph, so a newly <c>[ConfigField(Secret = true)]</c>
    /// annotated field is masked automatically.
    /// </summary>
    public static void Redact(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (var path in PlatformSecretPaths)
            ApplyRedact(config, path, 0, path.Terminal);
    }

    /// <summary>
    /// Deep-merges <paramref name="incoming"/> onto <paramref name="target"/> in place.
    /// Object values are merged recursively; scalar and array values replace. Keys
    /// present in <paramref name="target"/> but absent from <paramref name="incoming"/>
    /// are preserved (this is what protects omitted channel subtrees).
    /// </summary>
    public static void DeepMerge(JsonObject target, JsonObject incoming)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(incoming);
        foreach (var (key, incomingValue) in incoming)
        {
            if (incomingValue is JsonObject incomingObj && target[key] is JsonObject targetObj)
            {
                DeepMerge(targetObj, incomingObj);
            }
            else
            {
                target[key] = incomingValue?.DeepClone();
            }
        }
    }

    /// <summary>
    /// Restores real secrets on the whole-config document. Anywhere
    /// <paramref name="target"/> still holds the <see cref="Placeholder"/>, the
    /// corresponding value from <paramref name="existing"/> is copied back. Walks
    /// exactly the same reflection-discovered field paths as <see cref="Redact"/> so the
    /// two stay symmetric.
    /// </summary>
    public static void RestoreSecrets(JsonObject existing, JsonObject target)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(target);
        foreach (var path in PlatformSecretPaths)
            ApplyRestore(existing, target, path, 0, path.Terminal);
    }

    /// <summary>
    /// Applies redaction for an explicit secret-path set. Used by the public <see cref="Redact"/>
    /// (with the cached <see cref="PlatformConfig"/> set) and exposed internally so tests can drive
    /// the same engine over a synthetic graph.
    /// </summary>
    internal static void RedactPaths(JsonObject config, IReadOnlyList<SecretPath> paths)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
            ApplyRedact(config, path, 0, path.Terminal);
    }

    /// <summary>
    /// Applies secret restoration for an explicit secret-path set. Companion to
    /// <see cref="RedactPaths"/>.
    /// </summary>
    internal static void RestorePaths(JsonObject existing, JsonObject target, IReadOnlyList<SecretPath> paths)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
            ApplyRestore(existing, target, path, 0, path.Terminal);
    }

    // ── Path application ──────────────────────────────────────────────────────

    private static void ApplyRedact(JsonObject? target, SecretPath path, int index, SecretTerminal terminal)
    {
        if (target is null)
            return;

        var segment = path.Segments[index];
        var isLast = index == path.Segments.Length - 1;

        if (segment == "*")
        {
            // Dictionary section: recurse into every object-valued entry.
            foreach (var (_, child) in target)
                ApplyRedact(child as JsonObject, path, index + 1, terminal);
            return;
        }

        if (!isLast)
        {
            ApplyRedact(target[segment] as JsonObject, path, index + 1, terminal);
            return;
        }

        // Terminal segment.
        if (terminal == SecretTerminal.Scalar)
        {
            if (target[segment] is JsonValue)
                target[segment] = Placeholder;
        }
        else if (target[segment] is JsonObject dictionary)
        {
            foreach (var key in dictionary.Select(static pair => pair.Key).ToArray())
            {
                if (dictionary[key] is JsonValue)
                    dictionary[key] = Placeholder;
            }
        }
    }

    private static void ApplyRestore(JsonObject? existing, JsonObject? target, SecretPath path, int index, SecretTerminal terminal)
    {
        if (target is null)
            return;

        var segment = path.Segments[index];
        var isLast = index == path.Segments.Length - 1;

        if (segment == "*")
        {
            foreach (var (key, child) in target)
                ApplyRestore((existing?[key]) as JsonObject, child as JsonObject, path, index + 1, terminal);
            return;
        }

        if (!isLast)
        {
            ApplyRestore(existing?[segment] as JsonObject, target[segment] as JsonObject, path, index + 1, terminal);
            return;
        }

        // Terminal segment.
        if (terminal == SecretTerminal.Scalar)
        {
            if (IsPlaceholder(target[segment]) && existing?[segment] is JsonNode existingValue)
                target[segment] = existingValue.DeepClone();
        }
        else if (target[segment] is JsonObject targetDict)
        {
            var existingDict = existing?[segment] as JsonObject;
            foreach (var key in targetDict.Select(static pair => pair.Key).ToArray())
            {
                if (IsPlaceholder(targetDict[key]) && existingDict?[key] is JsonNode existingValue)
                    targetDict[key] = existingValue.DeepClone();
            }
        }
    }

    private static bool IsPlaceholder(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var s) && s == Placeholder;

    // ── Reflection discovery ──────────────────────────────────────────────────

    /// <summary>
    /// Enumerates every secret JSON path over the typed config graph rooted at
    /// <paramref name="root"/>. A property annotated with <c>[ConfigField(Secret = true)]</c>
    /// (or <c>Widget == ConfigFieldWidget.Secret</c>) contributes a path: a scalar string secret
    /// terminates as <see cref="SecretTerminal.Scalar"/>; a <c>Dictionary&lt;string, string&gt;</c>
    /// secret terminates as <see cref="SecretTerminal.DictionaryValues"/>. Dictionary-valued POCO
    /// sections contribute a <c>"*"</c> wildcard segment and are recursed into; nested POCOs are
    /// recursed into directly. Exposed internally so tests can assert the discovered set.
    /// </summary>
    internal static IReadOnlyList<SecretPath> DiscoverSecretPaths(Type root)
    {
        var paths = new List<SecretPath>();
        Walk(root, [], [], paths);
        return paths;
    }

    private static void Walk(Type type, string[] prefix, IReadOnlyCollection<Type> ancestry, List<SecretPath> sink)
    {
        // Guard against cycles in the type graph (the config graph is a tree today, but a future
        // self-referential POCO must not spin here).
        if (ancestry.Contains(type))
            return;

        var nextAncestry = new HashSet<Type>(ancestry) { type };

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var jsonName = ResolveJsonName(property);
            var segment = Append(prefix, jsonName);
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            var configField = property.GetCustomAttribute<ConfigFieldAttribute>();
            var isSecret = configField is not null &&
                (configField.Secret || configField.Widget == ConfigFieldWidget.Secret);

            if (isSecret)
            {
                // A string secret is a scalar; a string-valued dictionary marked secret has secret
                // values. Any other shape marked secret is treated as a scalar (best effort).
                var terminal = IsStringValuedDictionary(propertyType)
                    ? SecretTerminal.DictionaryValues
                    : SecretTerminal.Scalar;
                sink.Add(new SecretPath(segment, terminal));
            }

            // Recurse into nested config POCOs and dictionaries of POCOs to find deeper secrets.
            if (TryGetDictionaryValueType(propertyType, out var valueType) && IsConfigPoco(valueType))
            {
                Walk(valueType, Append(segment, "*"), nextAncestry, sink);
            }
            else if (IsConfigPoco(propertyType))
            {
                Walk(propertyType, segment, nextAncestry, sink);
            }
        }
    }

    private static string[] Append(string[] prefix, string segment)
    {
        var result = new string[prefix.Length + 1];
        Array.Copy(prefix, result, prefix.Length);
        result[prefix.Length] = segment;
        return result;
    }

    private static string ResolveJsonName(PropertyInfo property)
    {
        var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
        return attribute is not null
            ? attribute.Name
            : JsonNamingPolicy.CamelCase.ConvertName(property.Name);
    }

    // Only recurse into our own config POCOs; skip framework/primitive/JSON element types so the
    // walk stays bounded and does not chase into things like JsonElement or string[].
    private static bool IsConfigPoco(Type type)
        => type is { IsClass: true } &&
           type != typeof(string) &&
           type.Namespace is { } ns &&
           ns.StartsWith("BotNexus", StringComparison.Ordinal);

    private static bool IsStringValuedDictionary(Type type)
        => TryGetDictionaryValueType(type, out var valueType) && valueType == typeof(string);

    // Returns the value type of a string-keyed dictionary (Dictionary<string, V> / IDictionary<string, V>).
    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        var resolved = DictionaryValueTypeCache.GetOrAdd(type, static t =>
        {
            foreach (var candidate in EnumerateSelfAndInterfaces(t))
            {
                if (candidate.IsGenericType &&
                    candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) &&
                    candidate.GetGenericArguments()[0] == typeof(string))
                {
                    return candidate.GetGenericArguments()[1];
                }
            }

            return null;
        });

        valueType = resolved ?? typeof(object);
        return resolved is not null;
    }

    private static IEnumerable<Type> EnumerateSelfAndInterfaces(Type type)
    {
        if (type.IsInterface)
            yield return type;
        foreach (var iface in type.GetInterfaces())
            yield return iface;
    }
}
