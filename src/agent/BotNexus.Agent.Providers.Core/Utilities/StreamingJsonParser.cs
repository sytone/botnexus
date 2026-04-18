using System.Text;
using System.Text.Json;

namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// Best-effort parser for streaming/partial JSON.
/// Port of pi-mono's utils/json-parse.ts.
/// Handles incomplete JSON from tool call argument streaming.
/// </summary>
public static class StreamingJsonParser
{
    /// <summary>
    /// Parse partial/streaming JSON into a dictionary.
    /// Returns an empty dictionary if parsing fails completely.
    /// </summary>
    public static Dictionary<string, object?> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var repaired = RepairJson(json);

        try
        {
            var element = JsonDocument.Parse(repaired).RootElement;
            if (element.ValueKind == JsonValueKind.Object)
                return ElementToDictionary(element);

            return new Dictionary<string, object?>
            {
                ["$value"] = ConvertElement(element)
            };
        }
        catch (JsonException)
        {
            // Parsing failed even after repair
        }

        return [];
    }

    private static string RepairJson(string json)
    {
        var trimmed = json.Trim();
        if (trimmed.Length == 0)
            return "{}";

        // Remove trailing commas before closing brackets
        var sb = new StringBuilder(trimmed);
        RemoveTrailingCommas(sb);

        // Close any unclosed strings, arrays, and objects
        CloseUnclosedStructures(sb);

        return sb.ToString();
    }

    private static void RemoveTrailingCommas(StringBuilder sb)
    {
        for (var i = sb.Length - 1; i >= 0; i--)
        {
            if (sb[i] == ',' && IsFollowedByClosingBracket(sb, i + 1))
            {
                sb.Remove(i, 1);
            }
        }
    }

    private static bool IsFollowedByClosingBracket(StringBuilder sb, int start)
    {
        for (var i = start; i < sb.Length; i++)
        {
            if (char.IsWhiteSpace(sb[i])) continue;
            return sb[i] is '}' or ']';
        }
        return true; // end of string counts
    }

    private static void CloseUnclosedStructures(StringBuilder sb)
    {
        var inString = false;
        var escape = false;
        var stack = new Stack<char>();

        for (var i = 0; i < sb.Length; i++)
        {
            var c = sb[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString) continue;

            switch (c)
            {
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']' when stack.Count > 0: stack.Pop(); break;
            }
        }

        // Close unclosed string
        if (inString)
            sb.Append('"');

        // Close unclosed structures
        while (stack.Count > 0)
            sb.Append(stack.Pop());
    }

    private static Dictionary<string, object?> ElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertElement(prop.Value);
        }
        return dict;
    }

    private static object? ConvertElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.Array => element.Clone(),
        JsonValueKind.Object => ElementToDictionary(element),
        _ => element.ToString()
    };
}
