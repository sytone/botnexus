using System.Diagnostics;
using System.Text.Json;

namespace BotNexus.Agent.Providers.Copilot.Telemetry;

/// <summary>
/// Emits the contents of a <see cref="CopilotUsage"/> as structured Activity
/// tags so cost/billing information ends up on the same trace as the rest of
/// a Copilot call. Tag namespace: <c>botnexus.copilot.usage.*</c>.
/// </summary>
/// <remarks>
/// The total cost is emitted as <c>botnexus.copilot.usage.total_nano_aiu</c>.
/// Per-token-type entries become three tags each:
/// <list type="bullet">
///   <item><c>botnexus.copilot.usage.tokens.{type}</c> — token_count</item>
///   <item><c>botnexus.copilot.usage.cost_per_batch.{type}</c> — cost_per_batch</item>
///   <item><c>botnexus.copilot.usage.batch_size.{type}</c> — batch_size</item>
/// </list>
/// Token-type names are taken verbatim from the wire so any new types Copilot
/// introduces will surface automatically.
/// </remarks>
public static class CopilotUsageActivity
{
    private const string TagPrefix = "botnexus.copilot.usage.";

    /// <summary>
    /// Attach the contents of <paramref name="usage"/> to <paramref name="activity"/>.
    /// No-op when either argument is null.
    /// </summary>
    public static void Emit(CopilotUsage? usage, Activity? activity)
    {
        if (usage is null || activity is null)
            return;

        activity.SetTag(TagPrefix + "total_nano_aiu", usage.TotalNanoAiu);

        foreach (var detail in usage.TokenDetails)
        {
            var type = detail.TokenType;
            activity.SetTag(TagPrefix + "tokens." + type, detail.TokenCount);
            activity.SetTag(TagPrefix + "cost_per_batch." + type, detail.CostPerBatch);
            activity.SetTag(TagPrefix + "batch_size." + type, detail.BatchSize);
        }
    }

    /// <summary>
    /// Convenience wrapper: parse <paramref name="root"/> with
    /// <see cref="CopilotUsageParser.TryParse"/> and emit to
    /// <paramref name="activity"/> if a <c>copilot_usage</c> object is present.
    /// Safe to call on every SSE chunk — non-Copilot or chunks without the
    /// field are ignored.
    /// </summary>
    public static void TryParseAndEmit(JsonElement root, Activity? activity)
    {
        if (activity is null)
            return;
        if (CopilotUsageParser.TryParse(root, out var usage))
            Emit(usage, activity);
    }
}
