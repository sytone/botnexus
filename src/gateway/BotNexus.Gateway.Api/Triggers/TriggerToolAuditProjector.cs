using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Api.Triggers;

/// <summary>
/// Shared execution-layer projection of a blocking agent run's tool timeline into durable
/// <see cref="MessageRole.Tool"/> session-history rows. This is the #2127 slice that moves the
/// tool-audit projection off each individual trigger and into one shared sink, so every blocking
/// <c>PromptAsync</c> caller (cron, soul, heartbeat) records the same audit rows the interactive
/// streaming path persists.
/// </summary>
/// <remarks>
/// Historically only <c>CronTrigger</c> (issue #2118) projected the tool timeline; the soul and
/// heartbeat blocking paths saved only user/assistant text, so a tool that ran during those turns
/// left no durable audit record. Centralising the projection here removes that per-caller drift and
/// makes the audit invariant independent of which trigger initiated the run.
/// </remarks>
public static class TriggerToolAuditProjector
{
    /// <summary>
    /// Projects the tool calls carried on an <see cref="AgentResponse"/> into ordered
    /// <see cref="MessageRole.Tool"/> history entries, mirroring the tool rows the interactive
    /// streaming path persists (issue #2118 / #2127). Each call yields a single row carrying the
    /// tool call id, name, serialized arguments, result content, and error state. A call that never
    /// completed (cancelled/timed-out mid-flight, <see cref="AgentToolCallInfo.IsIncomplete"/>) is
    /// rendered with a synthesized "did not complete" body and an error flag so the transcript
    /// stays consistent with the streaming orphan-synthesis behaviour.
    /// </summary>
    /// <param name="response">The blocking-run response whose <see cref="AgentResponse.ToolCalls"/> are projected.</param>
    /// <returns>Ordered tool-history entries, one per tool call, in execution order.</returns>
    public static IEnumerable<SessionEntry> ProjectToolEntries(AgentResponse response)
    {
        foreach (var call in response.ToolCalls)
        {
            var content = call.IsIncomplete
                ? $"Tool '{call.ToolName}' did not complete - result synthesized for transcript consistency."
                : call.ResultContent
                    ?? (call.IsError ? "Tool execution failed." : "Tool execution completed.");

            yield return new SessionEntry
            {
                Role = MessageRole.Tool,
                Content = content,
                ToolName = call.ToolName,
                ToolCallId = call.ToolCallId,
                ToolArgs = call.Arguments,
                ToolIsError = call.IsError
            };
        }
    }

    /// <summary>
    /// True when the run executed at least one tool. Used by the heartbeat ack-prune guard so a
    /// silent-but-acted turn (tools ran, then an ack) is never pruned - pruning it would erase the
    /// only durable record that side-effecting tools executed (issue #2127 addendum finding 1).
    /// </summary>
    /// <param name="response">The blocking-run response to inspect.</param>
    /// <returns><see langword="true"/> when any tool call was recorded.</returns>
    public static bool HasToolActivity(AgentResponse response) => response.ToolCalls.Count > 0;
}
