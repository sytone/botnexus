using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.AgentExchange;

/// <summary>
/// The lifecycle phase an <see cref="AgentExchangeProgressEvent"/> reports.
/// </summary>
/// <remarks>
/// Deliberately a small closed set (#3176). The initiating conversation needs to answer four
/// questions — has it started, did it finish, did it break, was it cut short — and nothing more.
/// Per-turn token streaming is explicitly out of scope, so there is no "turn" phase carrying
/// child transcript content.
/// </remarks>
public enum AgentExchangeProgressPhase
{
    /// <summary>The child exchange has been created and pinned; the first turn is about to run.</summary>
    Started,

    /// <summary>The exchange ran to a normal conclusion (the target finished, or a single-shot call returned).</summary>
    Completed,

    /// <summary>The exchange threw. <see cref="AgentExchangeProgressEvent.Reason"/> carries the error message.</summary>
    Failed,

    /// <summary>
    /// The exchange was cut short by a guard rather than by the target agent — a turn cap, or a
    /// budget/cooldown admission refusal. Distinct from <see cref="Completed"/> precisely so a
    /// reader can tell "it finished" from "we stopped it" (AC4).
    /// </summary>
    Halted
}

/// <summary>
/// A single observable milestone in an agent-to-agent handoff, published into the
/// <em>initiating</em> conversation so a human watching that thread can see the delegated work
/// start, finish, fail, or get halted (#3176).
/// </summary>
/// <remarks>
/// This is a status record, not a transcript relay: it never carries the child agent's output.
/// The child <see cref="ConversationId"/> is the handle a reader uses to go and read the
/// exchange itself.
/// </remarks>
public sealed record AgentExchangeProgressEvent
{
    /// <summary>Which milestone this is.</summary>
    public required AgentExchangeProgressPhase Phase { get; init; }

    /// <summary>The delegating agent.</summary>
    public required AgentId InitiatorId { get; init; }

    /// <summary>The agent the work was handed to.</summary>
    public required AgentId TargetId { get; init; }

    /// <summary>
    /// The session in the initiating conversation that made the <c>agent_converse</c> call. This is
    /// the delivery target: progress is fanned out to that session's channel bindings.
    /// </summary>
    public SessionId? InitiatorSessionId { get; init; }

    /// <summary>The conversation the handoff was initiated from, used for stale-binding self-heal.</summary>
    public ConversationId? InitiatorConversationId { get; init; }

    /// <summary>
    /// The child exchange conversation. Null only for a pre-admission <see cref="AgentExchangeProgressPhase.Halted"/>
    /// event — a budget or cooldown refusal happens before any conversation is minted.
    /// </summary>
    public ConversationId? ChildConversationId { get; init; }

    /// <summary>The child exchange session. Null under the same pre-admission condition as <see cref="ChildConversationId"/>.</summary>
    public SessionId? ChildSessionId { get; init; }

    /// <summary>
    /// Machine-readable cause. For <see cref="AgentExchangeProgressPhase.Completed"/> this is the
    /// exchange's completion reason (<c>exchangeFinished</c> / <c>singleShot</c>); for
    /// <see cref="AgentExchangeProgressPhase.Halted"/> it names the guard (<c>maxTurnsReached</c>,
    /// <c>budgetExhausted</c>); for <see cref="AgentExchangeProgressPhase.Failed"/> it is the
    /// exception message.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Number of transcript entries produced, when known.</summary>
    public int? Turns { get; init; }

    /// <summary>
    /// Renders the one-line status message delivered to the initiating conversation.
    /// </summary>
    /// <remarks>
    /// Kept on the event (not in the notifier) so the wording is asserted by a domain test without
    /// standing up channel infrastructure, and so every producer emits an identical shape.
    /// </remarks>
    public string ToStatusLine()
    {
        var verb = Phase switch
        {
            AgentExchangeProgressPhase.Started => "started",
            AgentExchangeProgressPhase.Completed => "completed",
            AgentExchangeProgressPhase.Failed => "failed",
            AgentExchangeProgressPhase.Halted => "halted",
            _ => Phase.ToString().ToLowerInvariant()
        };

        var line = $"[handoff {verb}] {InitiatorId.Value} -> {TargetId.Value}";

        if (ChildConversationId is { } conversationId)
            line += $" (conversation {conversationId.Value}";
        if (ChildConversationId is not null && ChildSessionId is { } sessionId)
            line += $", session {sessionId.Value}";
        if (ChildConversationId is not null)
            line += ")";

        if (!string.IsNullOrWhiteSpace(Reason))
            line += $" reason: {Reason}";
        if (Turns is { } turns)
            line += $" turns: {turns}";

        return line;
    }
}
