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
            return CloneDictionary(agentOverrides!);

        if (agentOverrides is null || agentOverrides.Count == 0)
            return CloneDictionary(worldDefaults);

        var merged = new Dictionary<string, JsonElement>();
        var keys = new HashSet<string>(worldDefaults.Keys);
        keys.UnionWith(agentOverrides.Keys);

        foreach (var key in keys)
        {
            var hasWorld = worldDefaults.TryGetValue(key, out var worldValue);
            var hasAgent = agentOverrides.TryGetValue(key, out var agentValue);

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
                merged[key] = agentValue.Clone();
            }
        }

        return merged;
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
            return agent.Clone();

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
            merged[key] = value?.DeepClone();

        foreach (var (key, agentValue) in agent)
        {
            if (agentValue is JsonObject agentObject &&
                merged.TryGetPropertyValue(key, out var worldValue) &&
                worldValue is JsonObject worldObject)
            {
                merged[key] = MergeObjects(worldObject, agentObject);
            }
            else
            {
                merged[key] = agentValue?.DeepClone();
            }
        }

        return merged;
    }
}
