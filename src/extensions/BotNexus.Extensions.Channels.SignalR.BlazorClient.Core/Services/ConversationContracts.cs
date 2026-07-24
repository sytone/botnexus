using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

#pragma warning disable CS1591 // REST DTOs — public API via JSON deserialization

public sealed record ConversationSummaryDto(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("isDefault")] bool IsDefault,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("activeSessionId")] string? ActiveSessionId,
    [property: JsonPropertyName("bindingCount")] int BindingCount,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("kind")] string Kind = "HumanAgent",
    [property: JsonPropertyName("isPinned")] bool IsPinned = false,
    [property: JsonPropertyName("pinnedAt")] DateTimeOffset? PinnedAt = null,
    [property: JsonPropertyName("participants")] IReadOnlyList<ParticipantDto>? Participants = null);

public sealed record ParticipantDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string? Role = null);

public sealed record CreateConversationRequestDto(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("title")] string? Title);

public sealed record PatchConversationRequestDto(
    [property: JsonPropertyName("title")] string Title);

public sealed record ConversationResponseDto(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("isDefault")] bool IsDefault,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("activeSessionId")] string? ActiveSessionId,
    [property: JsonPropertyName("bindings")] IReadOnlyList<ConversationBindingDto> Bindings,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("modelOverride")] string? ModelOverride = null,
    [property: JsonPropertyName("thinkingOverride")] string? ThinkingOverride = null,
    [property: JsonPropertyName("contextWindowOverride")] int? ContextWindowOverride = null);

public sealed record SetConversationOverrideRequestDto(
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("thinking")] string? Thinking = null,
    [property: JsonPropertyName("contextWindow")] int? ContextWindow = null);

public sealed record ConversationBindingDto(
    [property: JsonPropertyName("bindingId")] string BindingId,
    [property: JsonPropertyName("channelType")] string ChannelType,
    [property: JsonPropertyName("channelAddress")] string ChannelAddress,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("threadingMode")] string ThreadingMode,
    [property: JsonPropertyName("displayPrefix")] string? DisplayPrefix,
    [property: JsonPropertyName("boundAt")] DateTimeOffset BoundAt);

public sealed record ConversationHistoryResponseDto(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("entries")] IReadOnlyList<ConversationHistoryEntryDto> Entries);

public sealed class ConversationHistoryEntryDto
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("toolArgs")]
    public string? ToolArgs { get; init; }

    [JsonPropertyName("toolIsError")]
    public bool ToolIsError { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("thinkingContent")]
    public string? ThinkingContent { get; init; }

    /// <summary>
    /// Orthogonal typed presentation kind of the entry (issue #2149): <c>message</c>,
    /// <c>subagent-completion</c>, or <c>subagent-response</c>. Null/absent from a legacy server
    /// is treated as <c>message</c> by the client.
    /// </summary>
    [JsonPropertyName("messageKind")]
    public string? MessageKind { get; init; }
}

public sealed record SessionHistoryResponseDto(
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("entries")] IReadOnlyList<SessionHistoryEntryDto> Entries);

public sealed class SessionHistoryEntryDto
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("toolArgs")]
    public string? ToolArgs { get; init; }

    [JsonPropertyName("toolIsError")]
    public bool ToolIsError { get; init; }

    [JsonPropertyName("thinkingContent")]
    public string? ThinkingContent { get; init; }

    /// <summary>
    /// Orthogonal typed presentation kind of the entry (issue #2149): <c>message</c>,
    /// <c>subagent-completion</c>, or <c>subagent-response</c>. Null/absent is treated as
    /// <c>message</c> by the client.
    /// </summary>
    [JsonPropertyName("messageKind")]
    public string? MessageKind { get; init; }
}
