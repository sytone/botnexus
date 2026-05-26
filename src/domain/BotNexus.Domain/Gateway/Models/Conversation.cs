using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Domain model for a conversation — a named, persistent grouping of one or more sessions
/// across potentially multiple channels.
/// </summary>
public sealed record Conversation
{
    /// <summary>Gets or sets the unique conversation identifier.</summary>
    public ConversationId ConversationId { get; set; }

    /// <summary>Gets or sets the agent that owns this conversation.</summary>
    public AgentId AgentId { get; set; }

    /// <summary>Gets or sets the human-readable title of this conversation.</summary>
    public string Title { get; set; } = "New conversation";

    /// <summary>Gets or sets the persisted description of this conversation's intent.</summary>
    public string? Purpose { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the agent's default conversation.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets the lifecycle status of this conversation.</summary>
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;

    /// <summary>Gets or sets when this conversation was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets when this conversation was last modified.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the session currently active within this conversation, if any.</summary>
    public SessionId? ActiveSessionId { get; set; }

    /// <summary>Gets or sets the channel bindings that route messages into and out of this conversation.</summary>
    public List<ChannelBinding> ChannelBindings { get; set; } = [];

    /// <summary>Gets or sets arbitrary extension metadata for this conversation.</summary>
    public Dictionary<string, object?> Metadata { get; set; } = [];

    /// <summary>Gets or sets the last canvas HTML rendered for this conversation, if any.</summary>
    public string? CanvasHtml { get; set; }

    /// <summary>Gets or sets conversation-scoped instructions injected into the system prompt on session start.</summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Gets or sets the citizen that opened this conversation — the user who sent the first
    /// inbound message, or the agent that programmatically created it (via <c>conversation_new</c>
    /// tool calls, heartbeats, cron triggers, etc.). Set by the router on creation and treated as
    /// write-once provenance; producers must not overwrite it on subsequent saves. <c>null</c> for
    /// legacy conversations created before this field existed and for paths where the creator's
    /// identity is not yet authenticated (see issue #527 for the HTTP create-endpoint follow-up).
    /// </summary>
    /// <remarks>
    /// This is distinct from <see cref="AgentId"/>, which is the agent that <em>owns</em> the
    /// conversation. For agent-initiated conversations the two are typically the same citizen, but
    /// they are not required to be — e.g. a heartbeat-triggered conversation may be initiated by a
    /// system agent yet owned by the target user-facing agent.
    /// </remarks>
    public CitizenId? Initiator { get; set; }

    /// <summary>
    /// Gets or sets the citizen-pairing discriminator for this conversation. Defaults to
    /// <see cref="ConversationKind.HumanAgent"/> so pre-Phase-4 rows deserialize unchanged.
    /// Set explicitly when a non-default pairing creates the conversation (e.g.
    /// <c>IAgentExchangeService.ConverseAsync</c> sets <see cref="ConversationKind.AgentAgent"/>;
    /// sub-agent spawn sets <see cref="ConversationKind.AgentSubAgent"/>).
    /// </summary>
    /// <remarks>
    /// Authoritative replacement for the historical "infer pairing from <c>SessionId</c> substring"
    /// shape (see F-3). Read by the portal/list/permission layers without having to walk session
    /// ids.
    /// </remarks>
    public ConversationKind Kind { get; set; } = ConversationKind.HumanAgent;
}
