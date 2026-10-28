using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Restores explicit empty collection and null shapes lost by the framework's flattened binding.
/// Absent values and ordinary populated values remain owned by the composed binder.
/// </summary>
internal static class CollectionShapeConfiguration
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    internal static void Apply(IReadOnlyList<IConfigurationProvider> providers, PlatformConfig config)
    {
        for (var index = 0; index < providers.Count; index++)
        {
            if (providers[index] is not IAcceptedRawConfigDocumentProvider raw
                || raw.GetAcceptedRawDocument() is not { } document)
                continue;
            using var parsed = JsonDocument.Parse(document.ToJsonString());
            Visit(config, parsed.RootElement, "", providers, index);
            if (parsed.RootElement.TryGetProperty("agents", out var agents)
                && agents.ValueKind == JsonValueKind.Object
                && agents.TryGetProperty("defaults", out var defaults)
                && config.AgentDefaults is { } target)
                Visit(target, defaults, "agents:defaults", providers, index);
        }
    }

    private static void Visit(object target, JsonElement raw, string path,
        IReadOnlyList<IConfigurationProvider> providers, int sourceIndex)
    {
        if (target is JsonElement || raw.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return;

        if (target is IDictionary dictionary)
        {
            foreach (var entry in raw.EnumerateObject())
            {
                if (dictionary.Contains(entry.Name) && dictionary[entry.Name] is { } child)
                    Visit(child, entry.Value, Join(path, entry.Name), providers, sourceIndex);
            }
            return;
        }

        if (target is IList list && raw.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var element in raw.EnumerateArray())
            {
                if (index < list.Count && list[index] is { } child)
                    Visit(child, element, Join(path, index.ToString(System.Globalization.CultureInfo.InvariantCulture)), providers, sourceIndex);
                index++;
            }
            return;
        }

        if (raw.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0
                || property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                continue;
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            var matches = raw.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
                continue;
            var entry = matches[0];
            var propertyPath = Join(path, entry.Name);
            var type = property.PropertyType;
            var collection = type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);
            var empty = entry.Value.ValueKind == JsonValueKind.Array && entry.Value.GetArrayLength() == 0
                || entry.Value.ValueKind == JsonValueKind.Object && !entry.Value.EnumerateObject().Any();
            if (collection && (empty || entry.Value.ValueKind == JsonValueKind.Null))
            {
                var opaqueBag = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                    && type.GetGenericArguments()[1] == typeof(JsonElement);
                if (opaqueBag && HasLaterRawValue(providers, sourceIndex, propertyPath))
                    continue;
                if (HasLaterValue(providers, sourceIndex, propertyPath))
                {
                    // Non-raw providers have no subsequent accepted-document visit to apply
                    // their explicit whole-collection null. Do not retain pre-reset children.
                    for (var later = providers.Count - 1; later > sourceIndex; later--)
                    {
                        if (!providers[later].TryGet(propertyPath, out var whole))
                            continue;
                        if (whole is null && !providers[later].GetChildKeys([], propertyPath).Any())
                            property.SetValue(target, null);
                        break;
                    }
                    continue;
                }
                var replacement = entry.Value.ValueKind == JsonValueKind.Null
                    ? null : JsonSerializer.Deserialize(entry.Value.GetRawText(), type, Options);
                var overlays = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var index = sourceIndex + 1; index < providers.Count; index++)
                    CollectChildren(providers[index], propertyPath, propertyPath, overlays);
                if (overlays.Count > 0)
                {
                    // Rebind only the suffix after the reset. Binding the original root would
                    // resurrect children belonging to providers before the explicit empty/null.
                    using var overlayRoot = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(overlays).Build();
                    replacement = overlayRoot.GetSection("collection").Get(type);
                }
                property.SetValue(target, replacement);
            }
            else if (property.GetValue(target) is { } child)
            {
                Visit(child, entry.Value, propertyPath, providers, sourceIndex);
            }
        }
    }

    private static bool HasLaterRawValue(IReadOnlyList<IConfigurationProvider> providers, int sourceIndex, string path)
    {
        for (var index = sourceIndex + 1; index < providers.Count; index++)
        {
            if (providers[index] is not IAcceptedRawConfigDocumentProvider raw
                || raw.GetAcceptedRawDocument() is not { } document)
                continue;
            using var parsed = JsonDocument.Parse(document.ToJsonString());
            var node = parsed.RootElement;
            var found = true;
            foreach (var segment in path.Split(':'))
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    found = false;
                    break;
                }
                var entry = node.EnumerateObject().FirstOrDefault(p => p.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (entry.Name is null)
                {
                    found = false;
                    break;
                }
                node = entry.Value;
            }
            if (found)
                return true;
        }
        return false;
    }

    private static bool HasLaterValue(IReadOnlyList<IConfigurationProvider> providers, int sourceIndex, string path)
    {
        for (var index = sourceIndex + 1; index < providers.Count; index++)
        {
            var provider = providers[index];
            // A later whole value or ancestor marker owns the subtree. Descendant overlays
            // alone are rebound separately so pre-reset siblings cannot leak through.
            for (var ancestor = path; ancestor.Length > 0;)
            {
                if (provider.TryGet(ancestor, out _))
                    return true;
                var separator = ancestor.LastIndexOf(':');
                ancestor = separator < 0 ? "" : ancestor[..separator];
            }
        }
        return false;
    }

    private static void CollectChildren(IConfigurationProvider provider, string path, string root,
        Dictionary<string, string?> values)
    {
        foreach (var child in provider.GetChildKeys([], path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = ConfigurationPath.Combine(path, child);
            if (provider.TryGet(key, out var value))
                values["collection" + key[root.Length..]] = value;
            CollectChildren(provider, key, root, values);
        }
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : path + ":" + key;
}
