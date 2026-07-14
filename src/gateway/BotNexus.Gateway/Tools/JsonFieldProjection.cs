using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Shared helper for optional field-selection ("sparse fieldsets") on API-backed
/// agent tool responses. Projects a serialized <see cref="JsonNode"/> down to a
/// caller-requested subset of top-level keys so the model can ask for only the
/// fields it needs while the default (no fields requested) stays the full object.
/// </summary>
/// <remarks>
/// This is intentionally reusable: any tool that serializes a response object (or a
/// list of them) can adopt the same <c>fields</c> pattern by routing its output
/// through <see cref="Project(JsonNode?, IReadOnlyCollection{string}?)"/>. Matching is
/// case-insensitive and lenient -- unknown field names are silently ignored rather than
/// raising an error, so a model requesting a field that does not exist on a given object
/// simply receives the fields that do exist.
/// </remarks>
public static class JsonFieldProjection
{
    /// <summary>
    /// Projects <paramref name="node"/> down to the requested top-level <paramref name="fields"/>.
    /// </summary>
    /// <param name="node">
    /// The serialized response node. May be a JSON object (projected to the subset of keys) or a
    /// JSON array (each object element is projected element-wise). Any other node kind, or
    /// <c>null</c>, is returned unchanged.
    /// </param>
    /// <param name="fields">
    /// The requested top-level keys. When <c>null</c> or empty the full <paramref name="node"/> is
    /// returned unchanged (non-breaking default). Matching is case-insensitive; unknown field names
    /// are ignored.
    /// </param>
    /// <returns>
    /// A new projected <see cref="JsonNode"/>, or the original node when no projection applies.
    /// </returns>
    public static JsonNode? Project(JsonNode? node, IReadOnlyCollection<string>? fields)
    {
        if (node is null || fields is null || fields.Count == 0)
            return node;

        // Deduplicate requested field names case-insensitively so a model that repeats a
        // field (or varies its casing) does not affect the output.
        var requested = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);

        return node switch
        {
            JsonArray array => ProjectArray(array, requested),
            JsonObject obj => ProjectObject(obj, requested),
            _ => node
        };
    }

    /// <summary>
    /// Reads the optional <c>fields</c> argument from a tool argument bag into a list of requested
    /// top-level key names. Accepts a JSON array of strings, a single string, or a comma-separated
    /// string; returns <c>null</c> when the argument is absent, null, or yields no usable names so
    /// callers fall back to the full-object default.
    /// </summary>
    public static IReadOnlyList<string>? ReadFields(IReadOnlyDictionary<string, object?> arguments, string key = "fields")
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
            return null;

        var names = new List<string>();

        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Array } arrayElement:
                foreach (var item in arrayElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                        names.Add(name);
                }
                break;
            case JsonElement { ValueKind: JsonValueKind.String } stringElement:
                AddSplit(names, stringElement.GetString());
                break;
            case IEnumerable<string> stringEnumerable:
                foreach (var name in stringEnumerable)
                    AddSplit(names, name);
                break;
            case string raw:
                AddSplit(names, raw);
                break;
        }

        return names.Count == 0 ? null : names;
    }

    private static void AddSplit(List<string> names, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            names.Add(part);
    }

    private static JsonArray ProjectArray(JsonArray array, HashSet<string> requested)
    {
        var projected = new JsonArray();
        foreach (var element in array)
        {
            projected.Add(element is JsonObject obj
                ? ProjectObject(obj, requested)
                : element?.DeepClone());
        }
        return projected;
    }

    private static JsonObject ProjectObject(JsonObject obj, HashSet<string> requested)
    {
        var projected = new JsonObject();
        foreach (var property in obj)
        {
            if (requested.Contains(property.Key))
                projected[property.Key] = property.Value?.DeepClone();
        }
        return projected;
    }
}
