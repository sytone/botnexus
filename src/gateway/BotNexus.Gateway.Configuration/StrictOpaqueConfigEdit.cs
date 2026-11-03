using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

// One atomic editor for opaque JSON preflight and the writer-locked latest document.
internal static class StrictOpaqueConfigEdit
{
    internal static bool TrySet(JsonNode? original, string path, JsonNode? replacement,
        out JsonNode? updated, out string error)
    {
        updated = original;
        if (!ConfigPathResolver.TryParsePath(path, out var tokens, out error))
            return false;
        var candidate = original?.DeepClone();
        if (!TryEditJson(ref candidate, tokens, 0, replacement, out error))
        {
            error = $"Path '{path}': {error}";
            return false;
        }
        updated = candidate;
        return true;
    }

    private static bool TryEditJson(ref JsonNode? current, IReadOnlyList<ConfigPathResolver.PathToken> tokens,
        int position, JsonNode? replacement, out string error)
    {
        error = string.Empty;
        if (position == tokens.Count)
        {
            current = replacement;
            return true;
        }

        var token = tokens[position];
        if (token.Name.Length > 0)
        {
            current ??= new JsonObject();
            if (current is not JsonObject obj)
            {
                error = $"Segment '{token.Raw}' requires a JSON object.";
                return false;
            }
            var key = obj.Select(pair => pair.Key).FirstOrDefault(key =>
                key.Equals(token.Name, StringComparison.OrdinalIgnoreCase)) ?? token.Name;
            var child = obj[key];
            // Split name[index] into member then index so nested arrays use the same checks.
            var next = tokens.ToList();
            next[position] = new ConfigPathResolver.PathToken(string.Empty, token.Index, token.Raw);
            var nextPosition = token.Index is null ? position + 1 : position;
            if (!TryEditJson(ref child, next, nextPosition, replacement, out error))
                return false;
            obj[key] = child?.DeepClone();
            return true;
        }

        current ??= new JsonArray();
        if (current is not JsonArray array)
        {
            error = $"Segment '{token.Raw}' requires a JSON array.";
            return false;
        }
        var index = token.Index.GetValueOrDefault();
        if (index < 0 || index > array.Count)
        {
            error = $"Index {index} is out of range. Current JSON array size is {array.Count}.";
            return false;
        }
        var element = index == array.Count ? null : array[index];
        if (!TryEditJson(ref element, tokens, position + 1, replacement, out error))
            return false;
        if (index == array.Count)
            array.Add(element?.DeepClone());
        else
            array[index] = element?.DeepClone();
        return true;
    }
}
