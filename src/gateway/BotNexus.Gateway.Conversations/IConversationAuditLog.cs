namespace BotNexus.Gateway.Conversations;

/// <summary>
/// Represents a single audit entry for a conversation mutation.
/// </summary>
public sealed record ConversationAuditEntry
{
    /// <summary>The conversation that was mutated.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The action performed: created, title_changed, purpose_set, instructions_set, archived, reset.</summary>
    public required string Action { get; init; }

    /// <summary>Who performed the action: agent ID, user ID, or 'api'.</summary>
    public required string Actor { get; init; }

    /// <summary>How the action was triggered: tool, rest-api, portal, signalr-hub.</summary>
    public required string Source { get; init; }

    /// <summary>Request, job, trace, or session identifier linking the transition to its caller.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Previous value (truncated to 200 chars) for change actions, null for create/archive.</summary>
    public string? PreviousValue { get; init; }

    /// <summary>New value (truncated to 200 chars) for change actions, null for archive.</summary>
    public string? NewValue { get; init; }

    /// <summary>When the action occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Stores and retrieves audit entries for conversation mutations.
/// </summary>
public interface IConversationAuditLog
{
    /// <summary>Records an audit entry.</summary>
    Task LogAsync(ConversationAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Retrieves audit entries for a conversation, newest first.</summary>
    Task<IReadOnlyList<ConversationAuditEntry>> GetAsync(string conversationId, int limit = 50, CancellationToken cancellationToken = default);
}
