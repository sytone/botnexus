using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Persistence interface for gateway conversations. Implementations control where
/// and how conversation data is stored.
/// </summary>
/// <remarks>
/// <para>Built-in implementations:</para>
/// <list type="bullet">
///   <item><b>InMemoryConversationStore</b> — Fast, non-durable. For development and testing.</item>
///   <item><b>FileConversationStore</b> — JSON file-backed. One file per conversation.</item>
/// </list>
/// <para>All implementations must be thread-safe.</para>
/// </remarks>
public interface IConversationStore
{
    /// <summary>
    /// Gets a specific conversation by ID. Returns <c>null</c> if not found.
    /// </summary>
    Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default);

    /// <summary>
    /// Lists all active conversations, optionally filtered by agent.
    /// </summary>
    Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default);

    /// <summary>
    /// Lists conversations <em>relevant to</em> the given citizen, in any status (including
    /// <see cref="ConversationStatus.Archived"/>) to match <see cref="ListAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>The set is the union of:</para>
    /// <list type="bullet">
    ///   <item><b>Initiator-match</b>: <c>Conversation.Initiator == citizen</c> — conversations
    ///     this citizen <em>opened</em>. Applies to both User and Agent species.</item>
    ///   <item><b>Owner-match</b>: when <c>citizen.Kind == Agent</c>, <c>Conversation.AgentId == citizen.AsAgent</c> —
    ///     conversations <em>owned</em> by this agent. There is no symmetric owner relationship
    ///     for User citizens (owning-by-user is not modelled), which makes this method
    ///     intentionally asymmetric across species.</item>
    ///   <item><b>Participant-match</b> (P9-F, issue #657): conversations whose
    ///     <see cref="Conversation.Participants"/> include this citizen. Replaces the
    ///     pre-P9-F per-session participant scan with a single indexed lookup against the
    ///     conversation-level participant set, and is the channel-facing "what is this
    ///     citizen in?" query.</item>
    /// </list>
    /// <para>The union is materialised as a distinct-by-<see cref="ConversationId"/> set;
    /// matching on more than one criterion does not produce duplicates.</para>
    /// </remarks>
    /// <param name="citizen">The citizen identity to query for; must be <see cref="CitizenId.IsValid"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default);

    /// <summary>
    /// Atomically merges the supplied participants into the conversation's
    /// <see cref="Conversation.Participants"/> set. The operation is idempotent: existing
    /// participants (matched by <c>SessionParticipant.CitizenId</c>) are left untouched, and
    /// re-supplying the same participant on a subsequent call is a no-op. The role on the
    /// first add wins; later calls do not overwrite the role label.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only sanctioned mutation path for
    /// <see cref="Conversation.Participants"/>. <see cref="SaveAsync"/> does not persist
    /// participant changes — implementations must treat the participant set as conversation
    /// state that is mutated only through this method so concurrent producers (multiple
    /// channels, agent exchanges, soul/cron triggers) cannot clobber each other or the
    /// rest of the conversation row.
    /// </para>
    /// <para>
    /// Each implementation is responsible for performing the merge atomically with respect
    /// to its own concurrency model (SQLite transaction, file lock, in-memory lock).
    /// </para>
    /// </remarks>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="participants">Participants to add. Empty enumerables are valid and produce no work.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddParticipantsAsync(
        ConversationId conversationId,
        IEnumerable<SessionParticipant> participants,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a new conversation. Throws if the ConversationId already exists.
    /// </summary>
    /// <param name="conversation">The conversation to create.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default);

    /// <summary>
    /// Persists the conversation state. Creates or updates as needed.
    /// </summary>
    /// <param name="conversation">The conversation to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(Conversation conversation, CancellationToken ct = default);

    /// <summary>
    /// Archives the conversation, setting its status to <see cref="ConversationStatus.Archived"/>.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default);

    /// <summary>
    /// Archives a conversation while recording the lifecycle source and correlation identifier.
    /// Production callers use this overload so every archive transition has central provenance.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="source">Stable source name such as <c>rest-api</c> or <c>retention</c>.</param>
    /// <param name="correlationId">Request, job, session, or trace identifier for the transition.</param>
    /// <param name="actor">The actor initiating the transition.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ArchiveAsync(
        ConversationId conversationId,
        string source,
        string? correlationId,
        string actor,
        CancellationToken ct = default)
        => ArchiveAsync(conversationId, ct);

    /// <summary>
    /// Resolves an active conversation for the given agent and channel binding details.
    /// Returns <c>null</c> if no matching conversation exists.
    /// </summary>
    /// <remarks>
    /// Native sub-addresses (e.g. Telegram forum topics) are encoded into
    /// <paramref name="channelAddress"/> by the originating adapter; the store matches
    /// on the opaque address only.
    /// </remarks>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="channelType">The channel type.</param>
    /// <param name="channelAddress">The channel-specific address (opaque, may be composite).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Conversation?> ResolveByBindingAsync(
        AgentId agentId,
        ChannelKey channelType,
        ChannelAddress channelAddress,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the <see cref="Conversation.UpdatedAt"/> timestamp to <see cref="DateTimeOffset.UtcNow"/>
    /// without loading or rewriting the full conversation state.
    /// <para>
    /// Intended for high-frequency call sites such as the message processing loop where
    /// bumping a timestamp is appropriate but a full <see cref="SaveAsync"/> round-trip
    /// (which reloads the cache and rewrites all columns) is unnecessary overhead.
    /// </para>
    /// <para>Silently no-ops when the conversation does not exist.</para>
    /// </summary>
    /// <param name="conversationId">The conversation to touch.</param>
    /// <param name="ct">Cancellation token.</param>
    Task TouchAsync(ConversationId conversationId, CancellationToken ct = default);

    /// <summary>
    /// Pins or unpins a conversation. When pinning, stamps PinnedAt to UtcNow.
    /// When unpinning, clears IsPinned and PinnedAt.
    /// Silently no-ops when the conversation does not exist.
    /// </summary>
    Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default);

    /// <summary>
    /// Returns lightweight summaries for all <em>active</em> conversations across the world,
    /// ordered most-recently-updated first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the global admin/debug listing. For agent-relative listings (the
    /// <c>GET /api/conversations?agentId=...</c> portal sidebar path), call
    /// <see cref="ListForCitizenAsync"/> instead and project the result — that path returns
    /// the union of owner-match and participant-match per W-1 (initiator + responder visibility),
    /// whereas this method intentionally has no agent filter so an owner-only contract cannot
    /// regress into the codebase by accident.
    /// </para>
    /// <para>
    /// Only conversations with <see cref="ConversationStatus.Active"/> are returned. Archived
    /// conversations are omitted.
    /// </para>
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default);

    // ── Canvas State ───────────────────────────────────────────────────────

    /// <summary>
    /// Gets the full canvas state dictionary for a conversation.
    /// Returns an empty dictionary if the conversation exists but has no state,
    /// or <c>null</c> if the conversation does not exist.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default);

    /// <summary>
    /// Sets (upserts) a single key in the canvas state for a conversation.
    /// Returns <c>true</c> if the operation succeeded, <c>false</c> if the conversation was not found.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="key">The state key.</param>
    /// <param name="value">The JSON value to store.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> SetCanvasStateKeyAsync(ConversationId conversationId, string key, JsonElement value, CancellationToken ct = default);

    /// <summary>
    /// Deletes a single key from the canvas state. No-op if the key or conversation does not exist.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="key">The state key to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteCanvasStateKeyAsync(ConversationId conversationId, string key, CancellationToken ct = default);

    /// <summary>
    /// Clears all canvas state for a conversation. No-op if the conversation has no state.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ClearCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default);
}
