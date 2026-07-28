using System.Text.Json.Nodes;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// Computes the set of JSON pointer-ish paths whose values differ between two config documents.
/// </summary>
/// <remarks>
/// The acceptance bar for #2066 is "only the intended semantic delta occurred". Asserting that
/// with per-field <c>ShouldBe</c> calls is unbounded work and, worse, silently misses the very
/// failures that matter: a subtree the assertion list forgot to mention. Diffing the whole
/// before/after document and asserting on the exact <em>set</em> of changed paths inverts that -
/// any unintended drop, clobber, or reordering surfaces as an unexpected path in the delta.
/// </remarks>
internal static class JsonDelta
{
    /// <summary>
    /// Returns every path (dotted, array indices in brackets) at which <paramref name="before"/>
    /// and <paramref name="after"/> differ, including additions and removals. An empty result
    /// means the two documents are semantically identical.
    /// </summary>
    internal static IReadOnlyList<string> Compute(JsonNode? before, JsonNode? after)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        Walk(before, after, string.Empty, paths);
        return [.. paths];
    }

    private static void Walk(JsonNode? before, JsonNode? after, string path, SortedSet<string> paths)
    {
        if (JsonNode.DeepEquals(before, after))
            return;

        if (before is JsonObject beforeObject && after is JsonObject afterObject)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var kvp in beforeObject)
                keys.Add(kvp.Key);
            foreach (var kvp in afterObject)
                keys.Add(kvp.Key);

            foreach (var key in keys)
            {
                beforeObject.TryGetPropertyValue(key, out var beforeChild);
                afterObject.TryGetPropertyValue(key, out var afterChild);
                Walk(beforeChild, afterChild, Join(path, key), paths);
            }

            return;
        }

        if (before is JsonArray beforeArray && after is JsonArray afterArray)
        {
            var length = Math.Max(beforeArray.Count, afterArray.Count);
            for (var i = 0; i < length; i++)
            {
                var beforeChild = i < beforeArray.Count ? beforeArray[i] : null;
                var afterChild = i < afterArray.Count ? afterArray[i] : null;
                Walk(beforeChild, afterChild, $"{path}[{i}]", paths);
            }

            return;
        }

        // Leaf difference, a type change, or an add/remove: record the path itself.
        paths.Add(path.Length == 0 ? "(root)" : path);
    }

    private static string Join(string path, string key)
        => path.Length == 0 ? key : $"{path}.{key}";
}
