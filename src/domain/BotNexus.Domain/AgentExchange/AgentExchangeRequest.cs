using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.AgentExchange;

/// <summary>
/// Represents a request for one agent to converse with another registered agent.
/// </summary>
public sealed record AgentExchangeRequest
{
    /// <summary>
    /// The initiating agent.
    /// </summary>
    public required AgentId InitiatorId { get; init; }

    /// <summary>
    /// The target agent.
    /// </summary>
    public required AgentId TargetId { get; init; }

    /// <summary>
    /// Opening message sent from initiator to target.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Optional objective used by the conversation manager.
    /// </summary>
    public string? Objective { get; init; }

    /// <summary>
    /// Maximum allowed back-and-forth turns.
    /// </summary>
    public int MaxTurns { get; init; } = 1;

    /// <summary>
    /// Current call chain used for depth and cycle detection.
    /// </summary>
    public IReadOnlyList<AgentId> CallChain { get; init; } = [];

    /// <summary>
    /// The session in the INITIATING conversation that issued this handoff, when known (#3176).
    /// </summary>
    /// <remarks>
    /// Purely observational: it is the delivery address for handoff progress events. Leaving it
    /// null (every pre-#3176 caller, and the cron action) silently disables progress emission and
    /// changes nothing else about the exchange.
    /// </remarks>
    public SessionId? InitiatorSessionId { get; init; }

    /// <summary>
    /// The conversation the handoff was initiated from, when known (#3176). Used alongside
    /// <see cref="InitiatorSessionId"/> to fan progress out to the originating thread.
    /// </summary>
    public ConversationId? InitiatorConversationId { get; init; }
}
