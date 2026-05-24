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
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

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
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

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
}
