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
/// <param name="Kind">
/// Discriminator (<c>HumanAgent</c>, <c>AgentAgent</c>, or <c>AgentSubAgent</c>) so consumers
/// like the portal can hide internal agent-to-agent transcripts from the user-facing conversation
/// list. Defaults to <c>HumanAgent</c> for back-compat with pre-Phase-4 stores.
/// </param>
/// <param name="Participants">
/// Citizen participants in this conversation — enables the portal to render a participant
/// roster (avatar chips) in the conversation header for multi-agent sessions.
/// </param>
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
    string? Purpose = null,
    string Kind = "HumanAgent",
    bool IsPinned = false,
    DateTimeOffset? PinnedAt = null,
    IReadOnlyList<ParticipantSummary>? Participants = null);

/// <summary>
/// Lightweight participant identity for conversation summary rendering.
/// </summary>
/// <param name="Kind">Citizen kind: "User" or "Agent".</param>
/// <param name="Id">The citizen identifier (agent id or user id).</param>
/// <param name="Role">Optional role label (e.g., "initiator", "peer").</param>
public sealed record ParticipantSummary(string Kind, string Id, string? Role = null);
