namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Publishes conversation lifecycle change notifications to external clients.
/// Implementations bridge gateway-level events to transport-specific channels.
/// </summary>
public interface IConversationChangeNotifier
{
    /// <summary>
    /// Notifies channel clients that a conversation has been created, updated, or archived.
    /// </summary>
    /// <param name="changeType">Change classification: created, updated, archived.</param>
    /// <param name="agentId">Agent that owns the conversation.</param>
    /// <param name="conversationId">Affected conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyConversationChangedAsync(string changeType, string agentId, string conversationId, CancellationToken cancellationToken = default);
}