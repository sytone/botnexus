namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Records a single mutation to a conversation for audit trail purposes.
/// </summary>
public sealed record ConversationAuditEntry
{
    /// <summary>Auto-incremented unique identifier.</summary>
    public long Id { get; init; }

    /// <summary>The conversation that was mutated.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The agent that owns the conversation.</summary>
    public required string AgentId { get; init; }

    /// <summary>
    /// The action performed: created, title_changed, purpose_changed, instructions_changed, archived, reset.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Who performed the action: agent ID, "portal", "api", or "system".
    /// </summary>
    public required string Actor { get; init; }

    /// <summary>
    /// Where the action originated: "tool", "rest-api", "signalr-hub", "auto-title", "system".
    /// </summary>
    public required string Source { get; init; }

    /// <summary>Previous value (truncated to 200 chars), or null for create actions.</summary>
    public string? PreviousValue { get; init; }

    /// <summary>New value (truncated to 200 chars).</summary>
    public string? NewValue { get; init; }

    /// <summary>When the action occurred.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
