using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;

namespace BotNexus.Gateway.Api.Triggers;

/// <summary>
/// #2641: single write-back point that copies a completed turn's cost measurements from an
/// <see cref="AgentResponse"/> onto the <see cref="InternalTriggerRequest"/> the cron scheduler
/// reads.
/// </summary>
/// <remarks>
/// <para>
/// Extracted rather than open-coded in each trigger because there are TWO triggers on this path -
/// <c>CronTrigger</c> and <c>SoulTrigger</c> - and soul-enabled agents (the ones running the
/// heaviest maintenance loops, and therefore the most expensive jobs on the platform) route through
/// the second one. The #2985 tool-count write-back had to be added to both separately, and the
/// migration-straggler pattern says the second copy is exactly what gets forgotten. One helper, one
/// architecture-visible call site each.
/// </para>
/// <para>
/// Every assignment preserves the null-means-not-measured rule: a provider that reported no usage
/// leaves the token fields null rather than stamping a zero that would present an unmeasured run as
/// a free one.
/// </para>
/// </remarks>
internal static class TriggerRunCostRecorder
{
    /// <summary>
    /// Copies tool count, turn count and aggregated token usage from <paramref name="response"/>
    /// onto <paramref name="request"/>. A null request or response is a no-op.
    /// </summary>
    /// <param name="request">The trigger request to stamp; may be null for non-cron callers.</param>
    /// <param name="response">The completed turn's response.</param>
    public static void Record(InternalTriggerRequest? request, AgentResponse? response)
    {
        if (request is null || response is null)
            return;

        // #2985 contract, unchanged: the tool count is reported even when it is zero, because a
        // genuine zero-tool turn is the signal the execution-class rule fires on.
        request.ToolInvocationCount = response.ToolCalls.Count;

        request.TurnCount = response.TurnCount;

        var usage = response.RunUsage;
        if (usage is null)
            return;

        // Cache reads/writes are already folded into InputTokens by the aggregation upstream.
        request.PromptTokens = usage.InputTokens;
        request.CompletionTokens = usage.OutputTokens;
    }
}
