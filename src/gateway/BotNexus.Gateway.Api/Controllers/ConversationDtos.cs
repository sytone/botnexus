namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Request body for creating a conversation.</summary>
/// <param name="AgentId">The agent that owns the new conversation.</param>
/// <param name="Title">Optional display title. When omitted, a default title is assigned.</param>
/// <param name="Purpose">Optional description of the conversation's intended use.</param>
/// <param name="Instructions">Optional conversation-scoped instructions injected into the system prompt on session start.</param>
public sealed record CreateConversationRequest(string AgentId, string? Title, string? Purpose = null, string? Instructions = null);

/// <summary>Request body for patching conversation metadata.</summary>
/// <param name="Title">Optional replacement display title.</param>
/// <param name="Purpose">Optional replacement description of the conversation's intended use.</param>
/// <param name="Instructions">Optional conversation-scoped instructions injected into the system prompt. Pass null to clear.</param>
public sealed record PatchConversationRequest(string? Title = null, string? Purpose = null, string? Instructions = null);

/// <summary>Request body for adding a channel binding.</summary>
public sealed record AddBindingRequest(
    string ChannelType,
    string? ChannelAddress,
    string? ThreadId,
    string? Mode,
    string? ThreadingMode,
    string? DisplayPrefix);

/// <summary>Full conversation response including bindings.</summary>
public sealed record ConversationResponse(
    string ConversationId,
    string AgentId,
    string Title,
    string? Purpose,
    string? Instructions,
    bool IsDefault,
    string Status,
    string? ActiveSessionId,
    IReadOnlyList<BindingResponse> Bindings,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Channel binding response.</summary>
public sealed record BindingResponse(
    string BindingId,
    string ChannelType,
    string ChannelAddress,
    string? ThreadId,
    string Mode,
    string ThreadingMode,
    string? DisplayPrefix,
    DateTimeOffset BoundAt);

/// <summary>Paginated conversation history response.</summary>
public sealed record ConversationHistoryResponse(
    string ConversationId,
    int TotalCount,
    int Offset,
    int Limit,
    IReadOnlyList<ConversationHistoryEntry> Entries);

/// <summary>A single history entry — either a message or a session boundary marker.</summary>
public sealed class ConversationHistoryEntry
{
    /// <summary>Entry kind: "message" or "boundary".</summary>
    public required string Kind { get; init; }

    /// <summary>The session this entry belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>Entry timestamp.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Message role (for kind = "message").</summary>
    public string? Role { get; init; }

    /// <summary>Message content (for kind = "message").</summary>
    public string? Content { get; init; }

    /// <summary>Tool name (for kind = "message" with tool role).</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool call correlation ID (for kind = "message" with tool role).</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Serialized JSON tool arguments (for ToolStart entries).</summary>
    public string? ToolArgs { get; init; }

    /// <summary>True if the tool call returned an error.</summary>
    public bool ToolIsError { get; init; }

    /// <summary>Reason for the boundary (for kind = "boundary").</summary>
    public string? Reason { get; init; }
}
