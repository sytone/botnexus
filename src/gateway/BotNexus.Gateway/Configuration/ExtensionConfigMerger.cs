using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

internal static class ExtensionConfigMerger
{
    /// <summary>
    /// Deep-merges world-level extension defaults with agent-level overrides.
    /// Agent values win on leaf conflicts. Objects merge recursively.
    /// Arrays and scalars are replaced wholesale by the agent override.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inheritance is tri-state</b> (issue #2706), matching
    /// <see cref="AgentConfigMerger"/> so operators get one mental model across the
    /// whole configuration surface:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Key absent</b> from the agent layer - the world-level value is inherited.</description></item>
    ///   <item><description><b>Key present with an explicit JSON <c>null</c></b> - the inherited world value is
    ///     <i>suppressed</i>. The key is removed from the merged result; it does not survive as a null leaf.</description></item>
    ///   <item><description><b>Key present with a value</b> - the agent value replaces the world value.</description></item>
    /// </list>
    /// <para>
    /// Suppression applies at every depth, including the extension id itself: an agent entry of
    /// <c>"someExtension": null</c> drops that extension's inherited configuration entirely. An explicit
    /// null with no world-level counterpart is a no-op rather than a null leaf, so <c>null</c> always means
    /// "do not inherit this" and never means "set this to null".
    /// </para>
    /// <para>
    /// Because <c>null</c> is reserved for suppression, an extension that needs to persist a genuine JSON
    /// null must model it explicitly (for example as an object with a discriminator) rather than relying
    /// on a bare <c>null</c> surviving the merge.
    /// </para>
    /// </remarks>
    public static Dictionary<string, JsonElement> Merge(
        Dictionary<string, JsonElement>? worldDefaults,
        Dictionary<string, JsonElement>? agentOverrides)
    {
        if ((worldDefaults is null || worldDefaults.Count == 0) &&
            (agentOverrides is null || agentOverrides.Count == 0))
        {
            return [];
        }

        if (worldDefaults is null || worldDefaults.Count == 0)
            return CloneDictionary(StripNulls(agentOverrides!));

        if (agentOverrides is null || agentOverrides.Count == 0)
            return CloneDictionary(worldDefaults);

        var merged = new Dictionary<string, JsonElement>();
        var keys = new HashSet<string>(worldDefaults.Keys);
        keys.UnionWith(agentOverrides.Keys);

        foreach (var key in keys)
        {
            var hasWorld = worldDefaults.TryGetValue(key, out var worldValue);
            var hasAgent = agentOverrides.TryGetValue(key, out var agentValue);

            // Tri-state: an explicit agent-level null suppresses the inherited entry
            // rather than overwriting it with null.
            if (hasAgent && agentValue.ValueKind == JsonValueKind.Null)
                continue;

            if (hasWorld && hasAgent)
            {
                merged[key] = DeepMergeElement(worldValue, agentValue);
            }
            else if (hasWorld)
            {
                merged[key] = worldValue.Clone();
            }
            else
            {
                merged[key] = StripNullsElement(agentValue);
            }
        }

        return merged;
    }

    /// <summary>
    /// Drops entries whose agent value is an explicit JSON null. With no world layer to
    /// suppress, "do not inherit this" degenerates to "this key is not present".
    /// </summary>
    private static Dictionary<string, JsonElement> StripNulls(Dictionary<string, JsonElement> source)
    {
        var result = new Dictionary<string, JsonElement>(source.Count);
        foreach (var (key, value) in source)
        {
            if (value.ValueKind == JsonValueKind.Null)
                continue;
            result[key] = StripNullsElement(value);
        }

        return result;
    }

    private static JsonElement StripNullsElement(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return value.Clone();

        var node = JsonNode.Parse(value.GetRawText())?.AsObject() ?? [];
        var stripped = MergeObjects([], node);
        using var document = JsonDocument.Parse(stripped.ToJsonString());
        return document.RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> CloneDictionary(Dictionary<string, JsonElement> source)
    {
        var clone = new Dictionary<string, JsonElement>(source.Count);
        foreach (var (key, value) in source)
            clone[key] = value.Clone();
        return clone;
    }

    private static JsonElement DeepMergeElement(JsonElement world, JsonElement agent)
    {
        if (world.ValueKind != JsonValueKind.Object || agent.ValueKind != JsonValueKind.Object)
            return StripNullsElement(agent);

        var worldObject = JsonNode.Parse(world.GetRawText())?.AsObject() ?? [];
        var agentObject = JsonNode.Parse(agent.GetRawText())?.AsObject() ?? [];
        var mergedObject = MergeObjects(worldObject, agentObject);
        using var document = JsonDocument.Parse(mergedObject.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonObject MergeObjects(JsonObject world, JsonObject agent)
    {
        var merged = new JsonObject();

        foreach (var (key, value) in world)
        {
            if (value is null)
                continue;
            merged[key] = value.DeepClone();
        }

        foreach (var (key, agentValue) in agent)
        {
            // Tri-state (#2706): an explicit JSON null on the agent side suppresses the
            // inherited key instead of writing a null leaf. Removing an absent key is a no-op.
            if (agentValue is null)
            {
                merged.Remove(key);
                continue;
            }

            if (agentValue is JsonObject agentObject &&
                merged.TryGetPropertyValue(key, out var worldValue) &&
                worldValue is JsonObject worldObject)
            {
                merged[key] = MergeObjects(worldObject, agentObject);
            }
            else if (agentValue is JsonObject standaloneObject)
            {
                // No world counterpart: still normalise nested suppression markers away.
                merged[key] = MergeObjects([], standaloneObject);
            }
            else
            {
                merged[key] = agentValue.DeepClone();
            }
        }

        return merged;
    }
}
