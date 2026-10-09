using System.Text;
using System.Text.Json;

namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>Versioned evidence-only projections; never rewrites transcript or execution payloads.</summary>
internal static class ToolProgressEvidenceProjection
{
    // TodoTool emits checkbox/text/id lines, with no observation-time counters. Retain all text,
    // including unsupported receipts, rather than guessing which timestamps might be incidental.
    internal static string TodoListV1(string text) => "todo-list:v1\n" + text;

    internal static string SubagentListV1(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("subAgents", out var rows)
                || rows.ValueKind != JsonValueKind.Array)
                return "subagents-raw:v1\n" + text;

            // SubAgentListTool's camelCase SubAgentRunDetail rows are recognized narrowly.
            // An unsupported row disables projection for the entire snapshot (fail closed).
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !HasNonemptyString(row, "subAgentId")
                    || !HasNonemptyString(row, "status")
                    || !HasCounterShape(row, "elapsedSeconds")
                    || !HasCounterShape(row, "remainingTimeSeconds"))
                    return "subagents-raw:v1\n" + text;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name == "subAgents")
                    {
                        writer.WriteStartArray();
                        foreach (var row in property.Value.EnumerateArray())
                            WriteCanonical(writer, row, stripObservationCounters: true);
                        writer.WriteEndArray();
                    }
                    else
                        WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
            }
            return "subagents-list:v1\n" + Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return "subagents-raw:v1\n" + text;
        }
    }

    private static bool HasNonemptyString(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool HasCounterShape(JsonElement row, string name)
        => !row.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Number or JsonValueKind.Null;

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, bool stripObservationCounters = false)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                // Only these two actual producer counters, only on the recognized direct row.
                // turnsUsed, remainingTurns, status, timestamps, and nested payload remain evidence.
                if (stripObservationCounters && property.Name is "elapsedSeconds" or "remainingTimeSeconds")
                    continue;
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
                WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else
            value.WriteTo(writer);
    }
}
