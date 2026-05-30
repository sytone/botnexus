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
}
