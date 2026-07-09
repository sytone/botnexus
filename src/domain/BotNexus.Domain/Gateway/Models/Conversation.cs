using System.Text.Json;
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

    /// <summary>
    /// Gets or sets the id of the world this conversation belongs to. Stamped by the
    /// conversation store on persistence using the gateway's resolved <see cref="WorldIdentity"/>
    /// (see <c>IWorldContext</c>). Defaults to empty string so pre-Phase-9 rows and in-memory
    /// constructs deserialise unchanged — stores lazily backfill the field on read when empty so
    /// older data converges to the current world id without an explicit migration script.
    /// </summary>
    /// <remarks>
    /// Cross-world relayed conversations (see <c>CrossWorldFederationController</c>) carry the
    /// receiving world id here; the source world id is preserved on <see cref="Metadata"/>
    /// (<c>sourceWorldId</c>). This keeps the typed field locally meaningful (the world that
    /// owns this row) while still allowing federation traces to be reconstructed from metadata.
    /// </remarks>
    public string WorldId { get; set; } = string.Empty;

    /// <summary>
    /// Gets the agent that owns this conversation. Write-once on construction — the
    /// <c>ConversationAgentIdImmutabilityArchitectureTests</c> fence pins this so a
    /// conversation's owning agent cannot drift after the row is persisted. This is the
    /// invariant that lets <see cref="IAgentIdentityResolver"/> cache the resolved
    /// value for the lifetime of the conversation (P9-H, issue #662, directive W-4).
    /// </summary>
    public AgentId AgentId { get; init; }

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

    /// <summary>
    /// Gets or sets the canvas key-value state for this conversation. Used by canvas tools
    /// to store and retrieve structured state that persists across sessions and gateway restarts.
    /// <c>null</c> means no state has been set; an empty dictionary means state was cleared.
    /// </summary>
    public Dictionary<string, JsonElement>? CanvasState { get; set; }

    /// <summary>Gets or sets conversation-scoped instructions injected into the system prompt on session start.</summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Gets or sets the per-conversation todo list state as an opaque JSON string, persisted on the
    /// conversation row alongside <see cref="CanvasHtml"/> and <see cref="Instructions"/>. Used by the
    /// <c>todo</c> tool to externalize a multi-step plan into structured state that survives sessions and
    /// compaction (see issue #1464). <c>null</c> means no todo state has been set. The concrete JSON shape
    /// (an <c>items</c> array of <c>{ id, text, status, createdAt, updatedAt }</c>) is owned by the todo
    /// tool that reads and writes it; the store treats this field as opaque text.
    /// </summary>
    public string? TodoJson { get; set; }

    /// <summary>
    /// Gets or sets the pending <c>ask_user</c> prompt for this conversation as an opaque JSON string,
    /// persisted on the conversation row alongside <see cref="CanvasHtml"/> and <see cref="TodoJson"/>.
    /// When an agent pauses on an interactive prompt, the full request (id, prompt, choices, timeout
    /// policy) is stored here so a reloaded tab, a newly-opened window, or a client that missed the live
    /// <c>UserInputRequired</c> event can hydrate the prompt on connect, and so the prompt survives a
    /// gateway restart (ask_user durability, issue #1488). <c>null</c> means no prompt is waiting. The
    /// concrete JSON shape (a serialized <c>AskUserRequest</c>) is owned by the ask_user flow that writes
    /// and clears it; the store treats this field as opaque text.
    /// </summary>
    public string? PendingAskUserJson { get; set; }

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

    /// <summary>Gets or sets whether this conversation is pinned to the top of the list.</summary>
    public bool IsPinned { get; set; }

    /// <summary>Gets or sets when this conversation was pinned. Null if not pinned. Used for ordering among pinned conversations (most recently pinned first).</summary>
    public DateTimeOffset? PinnedAt { get; set; }

    /// <summary>
    /// Gets or sets the citizens currently participating in this conversation. Populated as
    /// a side effect of inbound message routing and the agent-exchange handshake — see
    /// <c>IConversationStore.AddParticipantsAsync</c>, which is the only sanctioned mutation
    /// path (the <c>ConversationParticipantsMutationArchitectureTests</c> fence bans direct
    /// list mutation outside of conversation-store implementations and the one-shot backfill
    /// migration). Direct read access is fine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces the pre-P9-F <c>Session.Participants</c> field. The conversation is the
    /// durable owner of the participant set; a session is just a bounded transcript window
    /// inside it. Storing the set on the conversation means a citizen's "what am I in?"
    /// query (used by channels for inbox-style views) is one indexed lookup against this
    /// list rather than a fan-out scan over every session.
    /// </para>
    /// <para>
    /// The list deduplicates by <c>SessionParticipant.CitizenId</c>; the role on the first
    /// add for a given citizen is the role that wins (subsequent adds for the same citizen
    /// are merged-as-no-op so concurrent producers don't fight over a role label). See
    /// <c>SqliteConversationStore.AddParticipantsAsync</c> for the canonical implementation.
    /// </para>
    /// </remarks>
    public List<SessionParticipant> Participants { get; set; } = [];

    /// <summary>
    /// Gets or sets the per-conversation model override - the model identifier that beats the
    /// agent's configured default for every session in this conversation. <c>null</c> means no
    /// conversation-level override is set, so resolution falls through to the agent layer (see
    /// <c>ModelOverrideResolver</c>, PBI5 / issue #1706). Persisted on the conversation row
    /// alongside <see cref="Instructions"/> and <see cref="TodoJson"/>; the store treats it as an
    /// opaque model-id string and does not validate it (capability validation happens at the API
    /// boundary before the value is stored).
    /// </summary>
    public string? ModelOverride { get; set; }

    /// <summary>
    /// Gets or sets the per-conversation thinking-level override as its wire token (e.g.
    /// <c>minimal</c>, <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, <c>max</c>). <c>null</c>
    /// means no conversation-level override, so resolution falls through to the agent layer.
    /// Stored as the opaque token string mirroring the <see cref="ModelOverride"/> pattern so the
    /// domain layer does not take a dependency on the provider enum; the API boundary parses and
    /// validates it against the resolved model's capabilities before persistence (issue #1706).
    /// </summary>
    public string? ThinkingOverride { get; set; }

    /// <summary>
    /// Gets or sets the per-conversation context-window override in tokens. <c>null</c> means no
    /// conversation-level override, so resolution falls through to the agent layer. Validated at the
    /// API boundary against the resolved model's maximum context window before persistence
    /// (issue #1706).
    /// </summary>
    public int? ContextWindowOverride { get; set; }
}
