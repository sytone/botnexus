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
    /// Absolute instant at which this exchange must stop, when the caller imposes a deadline (#3515).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Absolute, not a <see cref="TimeSpan"/>.</strong> The value is passed across several
    /// frames before the turn engine arms it; a relative budget would silently re-base at each hop
    /// and grant more time than the caller allowed. An instant cannot drift.
    /// </para>
    /// <para>
    /// <strong>Why it exists at all.</strong> The turn engine decides whether a failed exchange is
    /// sealed by asking whether the CALLER cancelled. Before #3515 every deadline on this path was
    /// armed on a token linked <em>from</em> the caller's token, so a timer expiring was
    /// indistinguishable from a human pressing stop and the seal was skipped either way. Handing the
    /// engine the deadline as data lets it arm its OWN source, which the caller cannot cancel, so the
    /// two causes become separable evidence rather than one shared bit.
    /// </para>
    /// <para>
    /// Null (every pre-#3515 caller) means no engine-owned deadline: behaviour is exactly as before.
    /// </para>
    /// </remarks>
    public DateTimeOffset? Deadline { get; init; }

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
