using System.Text.Json;

namespace BotNexus.Agent.Providers.Copilot.Telemetry;

/// <summary>
/// Parses a <see cref="CopilotUsage"/> out of an arbitrary JSON root that may
/// or may not carry a <c>copilot_usage</c> object. Designed to be called on
/// every SSE chunk / non-stream response body the three Copilot providers see —
/// a missing or malformed <c>copilot_usage</c> is silently ignored.
/// </summary>
public static class CopilotUsageParser
{
    /// <summary>
    /// Look for <c>copilot_usage</c> directly on <paramref name="root"/> (the
    /// shape used by Chat Completions, the Responses <c>response.completed</c>
    /// event, and the Messages <c>message_delta</c> event) and try to
    /// materialise it.
    /// </summary>
    public static bool TryParse(JsonElement root, out CopilotUsage usage)
    {
        usage = null!;
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        if (!root.TryGetProperty("copilot_usage", out var node) || node.ValueKind != JsonValueKind.Object)
            return false;

        return TryParseObject(node, out usage);
    }

    /// <summary>
    /// Materialise <paramref name="node"/> as a <see cref="CopilotUsage"/>.
    /// <paramref name="node"/> must already point at the <c>copilot_usage</c>
    /// object itself.
    /// </summary>
    public static bool TryParseObject(JsonElement node, out CopilotUsage usage)
    {
        usage = null!;
        if (node.ValueKind != JsonValueKind.Object)
            return false;

        long total = 0;
        if (node.TryGetProperty("total_nano_aiu", out var totalProp)
            && totalProp.ValueKind == JsonValueKind.Number
            && totalProp.TryGetInt64(out var t))
        {
            total = t;
        }

        var details = new List<CopilotTokenDetail>();
        if (node.TryGetProperty("token_details", out var detailsProp)
            && detailsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in detailsProp.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                var type = entry.TryGetProperty("token_type", out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.String
                        ? typeProp.GetString() ?? string.Empty
                        : string.Empty;
                if (type.Length == 0)
                    continue;

                details.Add(new CopilotTokenDetail(
                    TokenType: type,
                    TokenCount: ReadInt64(entry, "token_count"),
                    BatchSize: ReadInt64(entry, "batch_size"),
                    CostPerBatch: ReadInt64(entry, "cost_per_batch")));
            }
        }

        usage = new CopilotUsage(total, details);
        return true;
    }

    private static long ReadInt64(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt64(out var v))
        {
            return v;
        }
        return 0;
    }
}
