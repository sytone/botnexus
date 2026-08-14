using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// The canonical read/write surface over a platform configuration document.
/// </summary>
/// <remarks>
/// <para>
/// #2887. Every consumer outside <c>BotNexus.Gateway.Configuration</c> addresses configuration by
/// canonical dotted path through this type. The raw traversal primitives
/// (<see cref="RawConfigPath"/>, <see cref="ConfigPathSyntax"/>) and the underlying
/// <see cref="JsonObject"/> are project-internal, so a consumer <em>cannot express</em> a
/// hand-rolled traversal - which is the #2764 defect made unrepresentable rather than merely fixed.
/// </para>
/// <para>
/// Every path is checked against <see cref="ConfigPathBinding"/> first. A path the typed graph does
/// not model is an explicit failure with a message naming the offending segment, never a
/// <see langword="null"/> that reads identically to "not configured".
/// </para>
/// <para>
/// <b>Entry helpers versus path helpers.</b> A <c>*Entry</c> method treats its key as a literal, so
/// an operator-supplied name containing <c>.</c> or <c>[</c> (a location or agent id) addresses the
/// single entry it names instead of being re-parsed into further path segments.
/// </para>
/// </remarks>
public sealed class ConfigDocument
{
    private readonly JsonObject _root;

    internal ConfigDocument(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
    }

    /// <summary>Parses a configuration document from JSON text. An empty or non-object document
    /// yields an empty configuration rather than throwing, matching how the loader treats a blank
    /// file.</summary>
    public static ConfigDocument Parse(string json)
        => new(string.IsNullOrWhiteSpace(json)
            ? new JsonObject()
            : JsonNode.Parse(json)?.AsObject() ?? new JsonObject());

    /// <summary>An empty configuration document.</summary>
    public static ConfigDocument Empty() => new(new JsonObject());

    /// <summary>The underlying document. Internal: handing a node out is what made hand-rolled
    /// traversal expressible in the first place.</summary>
    internal JsonObject Root => _root;

    /// <summary>
    /// The top-level keys of the document, in document order. Exposed so a caller can assert what a
    /// write did <em>not</em> create - a root-level block nothing binds is exactly the #2764 defect,
    /// and asserting its absence needs to enumerate the root without addressing an unrecognised path.
    /// </summary>
    public IReadOnlyList<string> RootKeys => _root.Select(pair => pair.Key).ToList();

    /// <summary>Serialises the document, indented, exactly as it is persisted.</summary>
    public string ToJsonString()
        => _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    // ---------------------------------------------------------------- reads

    /// <summary>
    /// Reads a string at <paramref name="path"/>. Returns <see langword="false"/> when the value is
    /// absent or is not a string; throws when the path is not one configuration binds, because that
    /// means a caller is reading somewhere the gateway never looks.
    /// </summary>
    public bool TryGetString(string path, out string? value)
    {
        value = null;
        RequireRecognised(path);

        return RawConfigPath.Get(_root, path) is JsonValue node
               && node.TryGetValue(out value);
    }

    /// <summary>Reads a bool at <paramref name="path"/>, or null when absent or not a bool.</summary>
    public bool? GetBool(string path)
    {
        RequireRecognised(path);

        return RawConfigPath.Get(_root, path) is JsonValue node && node.TryGetValue<bool>(out var value)
            ? value
            : null;
    }

    /// <summary>Reads an integer at <paramref name="path"/>, or null when absent or not a number.</summary>
    public int? GetInt(string path)
    {
        RequireRecognised(path);

        return RawConfigPath.Get(_root, path) is JsonValue node && node.TryGetValue<int>(out var value)
            ? value
            : null;
    }

    /// <summary>Returns true when a value - including an explicit JSON null - is present at
    /// <paramref name="path"/>.</summary>
    public bool Exists(string path)
    {
        RequireRecognised(path);
        return RawConfigPath.Exists(_root, path);
    }

    /// <summary>Returns true when an object exists at <paramref name="path"/>.</summary>
    public bool HasObject(string path)
    {
        RequireRecognised(path);
        return RawConfigPath.Get(_root, path) is JsonObject;
    }

    /// <summary>
    /// The string elements of the list at <paramref name="path"/>, or an empty list when absent.
    /// Non-string elements are skipped rather than throwing, matching how the binder treats a
    /// hand-edited list of mixed values.
    /// </summary>
    public IReadOnlyList<string> GetStringList(string path)
    {
        RequireRecognised(path);

        if (RawConfigPath.Get(_root, path) is not JsonArray array)
            return [];

        return array
            .Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
            .Where(text => text is not null)
            .Select(text => text!)
            .ToList();
    }

    /// <summary>Returns true when a non-empty list exists at <paramref name="path"/>.</summary>
    public bool HasNonEmptyList(string path)
    {
        RequireRecognised(path);
        return RawConfigPath.Get(_root, path) is JsonArray { Count: > 0 };
    }

    /// <summary>
    /// The keys of the object at <paramref name="path"/>, in document order, or an empty list when
    /// the section is absent. Casing is reported exactly as it appears on disk so callers can
    /// address and display the entry the operator actually wrote.
    /// </summary>
    public IReadOnlyList<string> GetEntryKeys(string path)
    {
        RequireRecognised(path);

        return RawConfigPath.Get(_root, path) is JsonObject section
            ? section.Select(pair => pair.Key).ToList()
            : [];
    }

    /// <summary>The number of entries in the object at <paramref name="path"/>; zero when absent.</summary>
    public int CountEntries(string path) => GetEntryKeys(path).Count;

    /// <summary>
    /// The on-disk key of <paramref name="sectionPath"/> matching <paramref name="key"/>
    /// case-insensitively, or null when the section or entry is absent.
    /// </summary>
    public string? FindEntryKey(string sectionPath, string key)
    {
        RequireRecognised(sectionPath);
        return RawConfigPath.FindEntryKey(_root, sectionPath, key);
    }

    /// <summary>Renders the entry at <paramref name="sectionPath"/>/<paramref name="key"/> as
    /// indented JSON for display, or null when absent.</summary>
    public string? DescribeEntry(string sectionPath, string key)
    {
        RequireRecognised(sectionPath);

        return RawConfigPath.GetEntry(_root, sectionPath, key)
            ?.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // --------------------------------------------------------------- writes

    /// <summary>
    /// Sets <paramref name="path"/> to <paramref name="value"/>. Returns false with a
    /// caller-presentable <paramref name="error"/> when the path is unrecognised, malformed, or the
    /// value is not a representable configuration value.
    /// </summary>
    public bool TrySet(string path, object? value, out string error)
    {
        if (!ConfigPathBinding.TryRecognise(path, out error))
            return false;

        if (!TryConvert(value, path, out var node, out error))
            return false;

        return RawConfigPath.TrySet(_root, path, node, out error);
    }

    /// <summary>
    /// Sets <paramref name="path"/> from an already type-checked configuration value, serialised
    /// through the platform's config write conventions (camelCase, nulls omitted).
    /// </summary>
    /// <remarks>
    /// <c>config set</c> coerces the operator's string against the typed graph first, so the value
    /// arriving here may be any modelled shape - a list, a nested settings object - not just a
    /// scalar. Serialising it is therefore correct, whereas <see cref="TrySet"/> deliberately
    /// refuses an arbitrary object so a caller cannot smuggle an unmodelled payload in.
    /// </remarks>
    public bool TrySetFrom(string path, object? value, out string error)
    {
        if (!ConfigPathBinding.TryRecognise(path, out error))
            return false;

        var node = value is null
            ? null
            : JsonSerializer.SerializeToNode(value, value.GetType(), WriteOptions);

        return RawConfigPath.TrySet(_root, path, node, out error);
    }

    /// <summary>Sets <paramref name="path"/> to an object built from <paramref name="values"/>.</summary>
    public bool TrySetMap(string path, ConfigValueMap values, out string error)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (!ConfigPathBinding.TryRecognise(path, out error))
            return false;

        if (!TryBuildObject(values, path, out var node, out error))
            return false;

        return RawConfigPath.TrySet(_root, path, node, out error);
    }

    /// <summary>
    /// Overlays <paramref name="values"/> onto the object at <paramref name="path"/>. Properties
    /// present on disk but absent from the patch survive untouched, so an update that supplies two
    /// fields cannot erase the rest.
    /// </summary>
    public bool TryPatch(string path, ConfigValueMap values, out string error)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (!ConfigPathBinding.TryRecognise(path, out error))
            return false;

        if (!TryBuildObject(values, path, out var node, out error))
            return false;

        return RawConfigPath.TryPatchObject(_root, path, node, out error);
    }

    /// <summary>
    /// Sets <paramref name="path"/>, throwing when the path is unrecognised or the value is not
    /// representable. Used where the caller has no error channel (a doctor fix), so a bad write
    /// surfaces as a failure instead of being silently skipped.
    /// </summary>
    public void Set(string path, object? value)
    {
        if (!TrySet(path, value, out var error))
            throw new InvalidOperationException(error);
    }

    /// <summary>Sets <paramref name="path"/> to an object built from <paramref name="values"/>,
    /// throwing on failure. See <see cref="Set"/>.</summary>
    public void SetMap(string path, ConfigValueMap values)
    {
        if (!TrySetMap(path, values, out var error))
            throw new InvalidOperationException(error);
    }

    /// <summary>Removes <paramref name="path"/>, throwing on failure. See <see cref="Set"/>.</summary>
    public void Remove(string path)
    {
        if (!TryRemove(path, out var error))
            throw new InvalidOperationException(error);
    }

    /// <summary>Removes <paramref name="path"/>. An absent path is a successful no-op so callers can
    /// express "ensure absent" idempotently.</summary>
    public bool TryRemove(string path, out string error)
    {
        if (!ConfigPathBinding.TryRecognise(path, out error))
            return false;

        return RawConfigPath.TryRemove(_root, path, out error);
    }

    /// <summary>Replaces the entry <paramref name="key"/> of <paramref name="sectionPath"/> with an
    /// object built from <paramref name="values"/>, creating the section when absent.</summary>
    public bool TrySetEntry(string sectionPath, string key, ConfigValueMap values, out string error)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (!ConfigPathBinding.TryRecognise(sectionPath, out error))
            return false;

        if (!TryBuildObject(values, sectionPath, out var node, out error))
            return false;

        return RawConfigPath.TrySetEntry(_root, sectionPath, key, node, out error);
    }

    /// <summary>
    /// Replaces the entry <paramref name="key"/> of <paramref name="sectionPath"/> with
    /// <paramref name="value"/> serialised through the platform's config write conventions
    /// (camelCase, nulls omitted). Used where the CLI already holds a typed config object.
    /// </summary>
    public bool TrySetEntryFrom<T>(string sectionPath, string key, T value, out string error)
    {
        if (!ConfigPathBinding.TryRecognise(sectionPath, out error))
            return false;

        var node = JsonSerializer.SerializeToNode(value, WriteOptions);
        return RawConfigPath.TrySetEntry(_root, sectionPath, key, node, out error);
    }

    /// <summary>Overlays <paramref name="values"/> onto the entry <paramref name="key"/> of
    /// <paramref name="sectionPath"/>, preserving entry properties the patch does not mention.</summary>
    public bool TryPatchEntry(string sectionPath, string key, ConfigValueMap values, out string error)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (!ConfigPathBinding.TryRecognise(sectionPath, out error))
            return false;

        if (!TryBuildObject(values, sectionPath, out var node, out error))
            return false;

        return RawConfigPath.TryPatchEntry(_root, sectionPath, key, node, out error);
    }

    /// <summary>Removes the entry <paramref name="key"/> from <paramref name="sectionPath"/>. A
    /// missing section or entry is a successful no-op.</summary>
    public bool TryRemoveEntry(string sectionPath, string key, out string error)
    {
        if (!ConfigPathBinding.TryRecognise(sectionPath, out error))
            return false;

        return RawConfigPath.TryRemoveEntry(_root, sectionPath, key, out error);
    }

    /// <summary>
    /// Replaces the entire document with <paramref name="replacement"/>. This is the whole-document
    /// rewrite <c>botnexus init</c> performs and nothing else may: every other write is targeted, so
    /// unknown keys, extension JSON and secrets survive.
    /// </summary>
    public void ReplaceWith(ConfigDocument replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        _root.Clear();
        foreach (var pair in replacement._root)
            _root[pair.Key] = pair.Value?.DeepClone();
    }

    // ---------------------------------------------------------- fresh install

    /// <summary>
    /// Builds the document a fresh install receives: the typed defaults plus the reserved
    /// <c>agents.defaults</c> block and the bundled platform agents.
    /// </summary>
    /// <remarks>
    /// This composition lives here rather than in the CLI (#2887) because it is the one place the
    /// generated document's shape is decided, and expressing it call-side required raw node
    /// assembly. <see cref="PlatformConfig.AgentDefaults"/> is <c>[JsonIgnore]</c> - the loader
    /// lifts it out of the agents dictionary - so it has to be injected after serialisation.
    /// </remarks>
    public static ConfigDocument CreateForFreshInstall(PlatformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var root = JsonSerializer.SerializeToNode(config, WriteOptions)?.AsObject() ?? new JsonObject();

        if (root["agents"] is JsonObject agents)
        {
            var replacement = new JsonObject
            {
                ["defaults"] = new JsonObject
                {
                    ["memory"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["indexing"] = "auto"
                    },
                    ["heartbeat"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["intervalMinutes"] = 30,
                        ["quietHours"] = new JsonObject
                        {
                            ["enabled"] = true,
                            ["start"] = "23:00",
                            ["end"] = "07:00"
                        }
                    }
                }
            };

            foreach (var pair in agents)
                replacement[pair.Key] = pair.Value?.DeepClone();

            // #2636: emit the bundled platform agents as complete, editable entries produced by the
            // same builder the startup reconciler uses, so a fresh install looks finished and the
            // reconciler then finds nothing to do.
            foreach (var pair in FreshInstallAgentDefaults.CreateBundledAgents())
                replacement[pair.Key] = pair.Value?.DeepClone();

            root["agents"] = replacement;
        }

        return new ConfigDocument(root);
    }

    // ------------------------------------------------------------- internals

    internal static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private void RequireRecognised(string path)
    {
        if (!ConfigPathBinding.TryRecognise(path, out var error))
            throw new InvalidOperationException(error);
    }

    private static bool TryBuildObject(ConfigValueMap values, string path, out JsonObject result, out string error)
    {
        result = new JsonObject();
        error = string.Empty;

        foreach (var pair in values)
        {
            if (!TryConvert(pair.Value, $"{path}.{pair.Key}", out var node, out error))
                return false;

            result[pair.Key] = node;
        }

        return true;
    }

    private static bool TryConvert(object? value, string path, out JsonNode? node, out string error)
    {
        node = null;
        error = string.Empty;

        switch (value)
        {
            case null:
                return true;
            case string text:
                node = JsonValue.Create(text);
                return true;
            case bool flag:
                node = JsonValue.Create(flag);
                return true;
            case int number:
                node = JsonValue.Create(number);
                return true;
            case long number:
                node = JsonValue.Create(number);
                return true;
            case double number:
                node = JsonValue.Create(number);
                return true;
            case decimal number:
                node = JsonValue.Create(number);
                return true;
            case ConfigValueMap map:
            {
                if (!TryBuildObject(map, path, out var nested, out error))
                    return false;
                node = nested;
                return true;
            }
            case IEnumerable<string> items:
                node = new JsonArray([.. items.Select(item => (JsonNode)JsonValue.Create(item)!)]);
                return true;
            default:
                error = $"Configuration value for '{path}' has unsupported type "
                        + $"'{value.GetType().Name}'. Supported: string, bool, number, string sequence, "
                        + "or a nested ConfigValueMap.";
                return false;
        }
    }
}
