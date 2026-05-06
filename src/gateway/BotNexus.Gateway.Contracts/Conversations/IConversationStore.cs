using BotNexus.Domain.Primitives;
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
    /// Gets a conversation by ID, or <c>null</c> if it doesn't exist.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default);

    /// <summary>
    /// Lists all conversations, optionally filtered by agent.
    /// </summary>
    /// <param name="agentId">If set, only returns conversations for this agent.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default);

    /// <summary>
    /// Gets the default active conversation for an agent, or creates one if it doesn't exist.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Conversation> GetOrCreateDefaultAsync(AgentId agentId, CancellationToken ct = default);

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
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="channelType">The channel type.</param>
    /// <param name="channelAddress">The channel-specific address.</param>
    /// <param name="threadId">The native thread id, or <c>null</c> to match on address only.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Conversation?> ResolveByBindingAsync(
        AgentId agentId,
        ChannelKey channelType,
        ChannelAddress channelAddress,
        ThreadId? threadId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns lightweight summaries for all conversations, optionally filtered by agent.
    /// </summary>
    /// <param name="agentId">If set, only returns summaries for this agent.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(AgentId? agentId = null, CancellationToken ct = default);
}
