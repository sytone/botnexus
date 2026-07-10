using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Domain session state without infrastructure concerns such as locks or replay buffering.
/// </summary>
public sealed record Session
{
    /// <summary>
    /// Gets or sets the session id.
    /// </summary>
    public SessionId SessionId { get; set; }

    // P9-H (#662): Session.AgentId was deleted. Agent ownership is durably owned by the
    // parent Conversation (Conversation.AgentId, init-only post P9-H). Resolve the owning
    // agent via IAgentIdentityResolver, or for store-backed reads via the hydrated
    // GatewaySession.AgentId (populated at construction by ISessionStore implementations
    // from Conversation.AgentId). This eliminates the duplicated ownership that allowed
    // a session's agent and its conversation's agent to drift, per directive W-4.

    /// <summary>
    /// Gets or sets the channel type.
    /// </summary>
    public ChannelKey? ChannelType { get; set; }

    /// <summary>
    /// Gets or sets the session type.
    /// </summary>
    public SessionType SessionType { get; set; } = SessionType.UserAgent;

    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public SessionStatus Status { get; set; } = SessionStatus.Active;

    /// <summary>
    /// True for user-facing turns that should drive memory flushes, warmup
    /// surfacing, and other interactive-only side-effects. Returns <c>false</c>
    /// when:
    /// <list type="bullet">
    /// <item>The session is not a <see cref="SessionType.UserAgent"/> conversation
    /// (agent-self / sub-agent / agent-agent are all considered non-interactive).</item>
    /// <item>The session is routed through the <c>cron</c> channel — P9-E (#645)
    /// collapses <c>SessionType.Cron</c> onto <see cref="SessionType.UserAgent"/>
    /// (cron is a proxy for the citizen who scheduled the job, per directive W-2),
    /// so the channel key is the durable cron-vs-interactive signal. Without this
    /// exclusion <see cref="SessionEndMemoryFlusher"/>/<see cref="PreCompactionMemoryFlusher"/>
    /// would start firing on every scheduled job.</item>
    /// </list>
    /// </summary>
    public bool IsInteractive =>
        SessionType.Equals(SessionType.UserAgent)
        && (!ChannelType.HasValue
            || !string.Equals(ChannelType.Value.Value, "cron", StringComparison.OrdinalIgnoreCase));

    // P9-F (#657): Session.Participants was deleted. Participants are now durably owned
    // by the parent Conversation (Conversation.Participants), mutated only via
    // IConversationStore.AddParticipantsAsync, and queried via
    // IConversationStore.ListForCitizenAsync. This eliminates the duplicated ownership
    // that made channel-side "what conversations is this citizen in?" queries quadratic.

    /// <summary>
    /// Gets or sets the created at.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the updated at.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the expires at.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the conversation this session belongs to. Conversation is the
    /// durable parent container; sessions are bounded transcripts inside a conversation.
    /// Always set — store implementations backfill any legacy unset values to the agent's
    /// default conversation before returning a session to callers (completed in Phase 9 /
    /// P9-B; issues #615, #627).
    /// </summary>
    public ConversationId ConversationId { get; set; }

    /// <summary>
    /// Gets or sets the metadata.
    /// </summary>
    public Dictionary<string, object?> Metadata { get; set; } = [];

    /// <summary>
    /// Typed agent-to-agent exchange-completion state (issue #612, CC-1). This is a projection
    /// over <see cref="Metadata"/>: it replaces the four loose <c>activeAgentExchangeId</c> /
    /// <c>finishedAgentExchangeId</c> / <c>finishedAgentExchangeReason</c> /
    /// <c>finishedAgentExchangeSummary</c> string keys that previously carried this state.
    /// Backing storage on <see cref="Metadata"/> keeps the state round-tripping through the
    /// existing session-store persistence unchanged; the getter migrates-on-read from the legacy
    /// loose keys so pre-CC-1 persisted rows keep working. <c>null</c> when no exchange completion
    /// state is present. See <see cref="AgentExchangeCompletionState"/>.
    /// </summary>
    public AgentExchangeCompletionState? ExchangeCompletion
    {
        get => AgentExchangeCompletionState.FromMetadata(Metadata);
        set => AgentExchangeCompletionState.WriteTo(Metadata, value);
    }

    /// <summary>
    /// Gets or sets the history.
    /// </summary>
    public List<SessionEntry> History { get; set; } = [];

    public int MessageCount => History.Count;
}
