namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Persists and queries conversation audit entries.
/// </summary>
public interface IConversationAuditStore
{
    /// <summary>Records a new audit entry.</summary>
    Task RecordAsync(ConversationAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Returns the audit trail for a given conversation, ordered most recent first.</summary>
    Task<IReadOnlyList<ConversationAuditEntry>> GetByConversationAsync(
        string conversationId,
        int limit = 50,
        CancellationToken cancellationToken = default);
}
