using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Audit;

namespace BotNexus.Gateway.Api.Triggers;

/// <summary>
/// Thin adapter over the single execution-layer <see cref="IToolAuditSink"/> (issue #2614).
/// </summary>
/// <remarks>
/// <para>
/// This type used to BE the blocking-path projection - a second producer of the same concept the
/// streaming helper produced, with its own format. #2614 collapses the two: the rendering now lives
/// in <see cref="DefaultToolAuditSink"/>, which both <c>StreamAsync</c> and <c>PromptAsync</c>
/// callers write through, and what remains here is a call-site-shaped adapter over that one sink.
/// </para>
/// <para>
/// The rows are byte-identical to the pre-#2614 blocking projection (same count, same order, same
/// text); the additive change is that arguments and results now pass through the shared
/// <see cref="Abstractions.Models.ToolInvocationRecordPolicy"/> redaction + byte budget on the way.
/// </para>
/// </remarks>
public static class TriggerToolAuditProjector
{
    /// <summary>
    /// Projects the tool calls carried on an <see cref="AgentResponse"/> into ordered
    /// <see cref="MessageRole.Tool"/> history entries by capturing the run's shared #2613 record
    /// timeline and handing it to the sink. Removing this call from a blocking trigger removes that
    /// trigger's only durable tool-audit record - the mutation #2614 AC5 pins.
    /// </summary>
    /// <param name="response">The blocking-run response whose <see cref="AgentResponse.ToolCalls"/> are projected.</param>
    /// <returns>Ordered tool-history entries, one per tool call, in execution order.</returns>
    public static IEnumerable<SessionEntry> ProjectToolEntries(AgentResponse response)
        => ProjectToolEntries(response, DefaultToolAuditSink.Instance);

    /// <summary>
    /// Sink-explicit overload used by composition-aware callers and tests.
    /// </summary>
    /// <param name="response">The blocking-run response.</param>
    /// <param name="sink">The execution-layer tool-audit sink.</param>
    /// <returns>Ordered tool-history entries, one per tool call, in execution order.</returns>
    public static IReadOnlyList<SessionEntry> ProjectToolEntries(AgentResponse response, IToolAuditSink sink)
        => sink.ProjectBlockingRun(sink.CaptureBlockingRun(response));

    /// <summary>
    /// True when the run executed at least one tool. Used by the heartbeat ack-prune guard so a
    /// silent-but-acted turn (tools ran, then an ack) is never pruned - pruning it would erase the
    /// only durable record that side-effecting tools executed (issue #2127 addendum finding 1).
    /// </summary>
    /// <param name="response">The blocking-run response to inspect.</param>
    /// <returns><see langword="true"/> when any tool call was recorded.</returns>
    public static bool HasToolActivity(AgentResponse response) => response.ToolCalls.Count > 0;
}
