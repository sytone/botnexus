namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Lightweight summary of a conversation for listing without full model hydration.
/// </summary>
/// <param name="ConversationId">The unique conversation identifier.</param>
/// <param name="AgentId">The agent that owns this conversation.</param>
/// <param name="Title">The human-readable title.</param>
/// <param name="IsDefault">Whether this is the agent's default conversation.</param>
/// <param name="Status">The lifecycle status as a string.</param>
/// <param name="ActiveSessionId">The currently active session id, if any.</param>
/// <param name="BindingCount">The number of channel bindings on this conversation.</param>
/// <param name="CreatedAt">When the conversation was created.</param>
/// <param name="UpdatedAt">When the conversation was last modified.</param>
/// <param name="Purpose">The persisted description of the conversation's intent.</param>
public sealed record ConversationSummary(
    string ConversationId,
    string AgentId,
    string Title,
    bool IsDefault,
    string Status,
    string? ActiveSessionId,
    int BindingCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Purpose = null);
