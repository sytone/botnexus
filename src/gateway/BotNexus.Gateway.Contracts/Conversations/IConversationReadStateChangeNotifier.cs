namespace BotNexus.Gateway.Abstractions.Conversations;

using BotNexus.Domain.Primitives;

/// <summary>
/// Delivers a best-effort invalidation when a reader's durable conversation cursor advances.
/// The implementation must address only that reader; the notification is a hint to reload the
/// durable state, not a replacement for it.
/// </summary>
public interface IConversationReadStateChangeNotifier
{
    /// <summary>Notifies one reader after a cursor advance has committed to its store.</summary>
    Task NotifyReadStateChangedAsync(
        ConversationReaderId readerId,
        ConversationId conversationId,
        ConversationReadPosition position,
        long version,
        CancellationToken cancellationToken = default);
}
