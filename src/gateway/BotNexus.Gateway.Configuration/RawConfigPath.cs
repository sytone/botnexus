using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Dotted-path mutation primitives over the <em>raw</em> platform config JSON document.
/// </summary>
/// <remarks>
/// <para>
/// Targeted config mutations (CLI <c>config set</c>, agent/provider/location add-update-remove)
/// historically round-tripped the whole document through the typed <see cref="PlatformConfig"/>
/// graph. That is lossy: anything the typed graph does not model - unknown root keys, unknown
/// child keys, extension-owned JSON, and the reserved <c>agents.defaults</c> entry the loader
/// lifts out into <see cref="PlatformConfig.AgentDefaults"/> - disappeared on the next write
/// (#2057).
/// </para>
/// <para>
/// These helpers instead address the exact node the operation means to change and leave every
/// other byte of the document alone. Key lookup is case-insensitive against the keys already on
/// disk so an existing <c>Gateway</c> key is updated in place rather than shadowed by a new
/// <c>gateway</c> sibling. Segments may be plain names (<c>gateway.listenUrl</c>) or name plus
/// list index (<c>gateway.cors.origins[0]</c>).
/// </para>
/// </remarks>
public static class RawConfigPath
{
    /// <summary>
    /// Sets <paramref name="value"/> at <paramref name="dottedPath"/>, creating any missing
    /// intermediate objects/arrays. Returns <see langword="false"/> with a caller-presentable
    /// <paramref name="error"/> when the path is malformed or collides with an incompatible
    /// existing node (e.g. indexing a scalar).
    /// </summary>
    public static bool TrySet(JsonObject root, string dottedPath, JsonNode? value, out string error)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!TryParse(dottedPath, out var segments, out error))
            return false;

        JsonNode current = root;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Count - 1;

            if (current is not JsonObject container)
            {
                error = $"Path '{dottedPath}' cannot be resolved: '{segment.Raw}' is not reachable through a JSON object.";
                return false;
            }

            var key = ResolveKey(container, segment.Name);

            if (segment.Index is null)
            {
                if (isLast)
                {
                    container[key] = value?.DeepClone();
                    return true;
                }

                current = EnsureObject(container, key);
                continue;
            }

            if (container[key] is not JsonArray array)
            {
                array = new JsonArray();
                container[key] = array;
            }

            var index = segment.Index.Value;
            while (array.Count <= index)
                array.Add(null);

            if (isLast)
            {
                array[index] = value?.DeepClone();
                return true;
            }

            if (array[index] is not JsonObject element)
            {
                element = new JsonObject();
                array[index] = element;
            }

            current = element;
        }

        error = $"Path '{dottedPath}' is empty.";
        return false;
    }

    /// <summary>
    /// Removes the node at <paramref name="dottedPath"/>. A path that does not exist is a no-op
    /// (returns <see langword="true"/>) so callers can express "ensure absent" idempotently.
    /// </summary>
    public static bool TryRemove(JsonObject root, string dottedPath, out string error)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!TryParse(dottedPath, out var segments, out error))
            return false;

        JsonNode? current = root;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Count - 1;

            if (current is not JsonObject container)
                return true;

            var key = ResolveKey(container, segment.Name);
            if (!container.ContainsKey(key))
                return true;

            if (segment.Index is null)
            {
                if (isLast)
                {
                    container.Remove(key);
                    return true;
                }

                current = container[key];
                continue;
            }

            if (container[key] is not JsonArray array || segment.Index.Value >= array.Count)
                return true;

            if (isLast)
            {
                array.RemoveAt(segment.Index.Value);
                return true;
            }

            current = array[segment.Index.Value];
        }

        return true;
    }

    /// <summary>
    /// Returns the node at <paramref name="dottedPath"/>, or <see langword="null"/> when any
    /// segment is absent. Used by patch-style mutations that must read the on-disk shape before
    /// overlaying only the fields the caller actually supplied.
    /// </summary>
    public static JsonNode? Get(JsonObject root, string dottedPath)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!TryParse(dottedPath, out var segments, out _))
            return null;

        JsonNode? current = root;
        foreach (var segment in segments)
        {
            if (current is not JsonObject container)
                return null;

            var key = ResolveKey(container, segment.Name);
            if (!container.ContainsKey(key))
                return null;

            current = container[key];

            if (segment.Index is null)
                continue;

            if (current is not JsonArray array || segment.Index.Value >= array.Count)
                return null;

            current = array[segment.Index.Value];
        }

        return current;
    }

    /// <summary>
    /// Overlays the properties of <paramref name="patch"/> onto the object at
    /// <paramref name="dottedPath"/>, creating it when absent. Properties present on disk but
    /// absent from the patch survive untouched - this is the "PATCH only what was supplied"
    /// behaviour provider and location updates require so capability fields the CLI does not
    /// model (reasoning, context window, and any future addition) are never erased.
    /// </summary>
    public static bool TryPatchObject(JsonObject root, string dottedPath, JsonObject patch, out string error)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(patch);

        var existing = Get(root, dottedPath) as JsonObject;
        var merged = existing?.DeepClone().AsObject() ?? new JsonObject();

        foreach (var property in patch)
        {
            var key = ResolveKey(merged, property.Key);
            merged[key] = property.Value?.DeepClone();
        }

        return TrySet(root, dottedPath, merged, out error);
    }

    /// <summary>
    /// Returns the entry <paramref name="key"/> of the object at <paramref name="sectionPath"/>,
    /// or <see langword="null"/> when either is absent.
    /// </summary>
    /// <remarks>
    /// The entry helpers treat <paramref name="key"/> as a literal rather than a dotted path, so
    /// user-supplied names containing <c>.</c> or <c>[</c> (a location name, for example) address
    /// the intended single entry instead of being re-parsed into further path segments.
    /// </remarks>
    public static JsonNode? GetEntry(JsonObject root, string sectionPath, string key)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (Get(root, sectionPath) is not JsonObject section)
            return null;

        return section[ResolveKey(section, key)];
    }

    /// <summary>
    /// Returns the on-disk key of <paramref name="sectionPath"/> that matches
    /// <paramref name="key"/> case-insensitively, or <see langword="null"/> when the section does
    /// not exist or has no such entry. Callers use this to report the canonical casing back to the
    /// user and to address the entry they actually found.
    /// </summary>
    public static string? FindEntryKey(JsonObject root, string sectionPath, string key)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (Get(root, sectionPath) is not JsonObject section)
            return null;

        var resolved = ResolveKey(section, key);
        return section.ContainsKey(resolved) ? resolved : null;
    }

    /// <summary>
    /// Replaces (or creates) the entry <paramref name="key"/> of the object at
    /// <paramref name="sectionPath"/>, creating the section when absent.
    /// </summary>
    public static bool TrySetEntry(JsonObject root, string sectionPath, string key, JsonNode? value, out string error)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!TryEnsureSection(root, sectionPath, out var section, out error))
            return false;

        section[ResolveKey(section, key)] = value?.DeepClone();
        return true;
    }

    /// <summary>
    /// Overlays <paramref name="patch"/> onto the entry <paramref name="key"/> of the object at
    /// <paramref name="sectionPath"/>. Entry properties absent from the patch survive, so an
    /// update that supplies two fields cannot erase the rest of the entry.
    /// </summary>
    public static bool TryPatchEntry(JsonObject root, string sectionPath, string key, JsonObject patch, out string error)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(patch);

        if (!TryEnsureSection(root, sectionPath, out var section, out error))
            return false;

        var resolvedKey = ResolveKey(section, key);
        var merged = section[resolvedKey] is JsonObject existing
            ? existing.DeepClone().AsObject()
            : new JsonObject();

        foreach (var property in patch)
            merged[ResolveKey(merged, property.Key)] = property.Value?.DeepClone();

        section[resolvedKey] = merged;
        return true;
    }

    /// <summary>
    /// Removes the entry <paramref name="key"/> from the object at <paramref name="sectionPath"/>.
    /// A missing section or entry is a no-op.
    /// </summary>
    public static bool TryRemoveEntry(JsonObject root, string sectionPath, string key, out string error)
    {
        ArgumentNullException.ThrowIfNull(root);
        error = string.Empty;

        if (Get(root, sectionPath) is not JsonObject section)
            return true;

        section.Remove(ResolveKey(section, key));
        return true;
    }

    private static bool TryEnsureSection(JsonObject root, string sectionPath, out JsonObject section, out string error)
    {
        section = root;

        if (Get(root, sectionPath) is JsonObject existing)
        {
            error = string.Empty;
            section = existing;
            return true;
        }

        if (!TrySet(root, sectionPath, new JsonObject(), out error))
            return false;

        if (Get(root, sectionPath) is not JsonObject created)
        {
            error = $"Unable to create config section '{sectionPath}'.";
            return false;
        }

        section = created;
        return true;
    }

    /// <summary>
    /// Finds the on-disk key matching <paramref name="name"/> case-insensitively, falling back to
    /// <paramref name="name"/> verbatim when the object has no such key yet. Keeps CLI mutations
    /// from creating a differently-cased duplicate of a key that already exists.
    /// </summary>
    public static string ResolveKey(JsonObject container, string name)
    {
        ArgumentNullException.ThrowIfNull(container);

        if (container.ContainsKey(name))
            return name;

        foreach (var property in container)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                return property.Key;
        }

        return name;
    }

    private static JsonObject EnsureObject(JsonObject container, string key)
    {
        if (container[key] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        container[key] = created;
        return created;
    }

    private static bool TryParse(string? dottedPath, out IReadOnlyList<PathSegment> segments, out string error)
    {
        error = string.Empty;
        segments = [];

        if (string.IsNullOrWhiteSpace(dottedPath))
        {
            error = "Key path is required.";
            return false;
        }

        // See ConfigPathSyntax: Split clamps an unbalanced ']' and ignores a leftover open
        // depth, so a malformed path would otherwise be written to the raw document under a key
        // the operator never named (#2605).
        if (!ConfigPathSyntax.TryValidateBrackets(dottedPath, out error))
            return false;

        var parsed = new List<PathSegment>();
        foreach (var raw in Split(dottedPath))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                error = "Key path contains an empty segment.";
                return false;
            }

            var bracket = trimmed.IndexOf('[', StringComparison.Ordinal);
            if (bracket < 0)
            {
                parsed.Add(new PathSegment(trimmed, null, trimmed));
                continue;
            }

            var close = trimmed.IndexOf(']', bracket + 1);
            if (close != trimmed.Length - 1 || bracket == 0)
            {
                error = $"Invalid segment '{trimmed}'. Use 'name[index]' format.";
                return false;
            }

            var indexText = trimmed[(bracket + 1)..close];
            if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                error = $"Invalid index '{indexText}' in segment '{trimmed}'.";
                return false;
            }

            parsed.Add(new PathSegment(trimmed[..bracket], index, trimmed));
        }

        segments = parsed;
        if (parsed.Count == 0)
        {
            error = "Key path is required.";
            return false;
        }

        return true;
    }

    // Splits on '.' at bracket depth zero so a dotted key inside an index expression is not
    // mistaken for a segment boundary.
    private static List<string> Split(string path)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        foreach (var ch in path.Trim())
        {
            if (ch == '.' && depth == 0)
            {
                if (current.Length > 0)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            if (ch == '[')
                depth++;
            else if (ch == ']')
                depth = Math.Max(0, depth - 1);

            current.Append(ch);
        }

        if (current.Length > 0)
            segments.Add(current.ToString());

        return segments;
    }

    private readonly record struct PathSegment(string Name, int? Index, string Raw);
}
