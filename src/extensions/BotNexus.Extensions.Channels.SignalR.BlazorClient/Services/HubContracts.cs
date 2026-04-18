using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

#pragma warning disable CS1591 // Client-side hub DTOs matching the server wire format

// ── Server → Client payloads ────────────────────────────────────────────

/// <summary>Payload sent via the <c>Connected</c> client method on hub connect.</summary>
public sealed record ConnectedPayload(
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("agents")] IReadOnlyList<AgentSummary> Agents,
    [property: JsonPropertyName("serverVersion")] string ServerVersion,
    [property: JsonPropertyName("capabilities")] HubCapabilities Capabilities);

/// <summary>Agent identity summary included in <see cref="ConnectedPayload"/>.</summary>
public sealed record AgentSummary(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("displayName")] string DisplayName);

/// <summary>Hub capabilities advertised on connect.</summary>
public sealed record HubCapabilities(
    [property: JsonPropertyName("multiSession")] bool MultiSession);

/// <summary>Payload sent via the <c>SessionReset</c> client method.</summary>
public sealed record SessionResetPayload(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("sessionId")] string SessionId);

/// <summary>Payload sent via the <c>ContentDelta</c> client method.</summary>
public sealed record ContentDeltaPayload(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("contentDelta")] string? ContentDelta);

/// <summary>Agent stream event for MessageStart, ThinkingDelta, ToolStart, ToolEnd, MessageEnd, Error.</summary>
public sealed record AgentStreamEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("contentDelta")]
    public string? ContentDelta { get; init; }

    [JsonPropertyName("thinkingContent")]
    public string? ThinkingContent { get; init; }

    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    [JsonPropertyName("toolArgs")]
    public IReadOnlyDictionary<string, object?>? ToolArgs { get; init; }

    [JsonPropertyName("toolResult")]
    public string? ToolResult { get; init; }

    [JsonPropertyName("toolIsError")]
    public bool? ToolIsError { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}

/// <summary>Sub-agent lifecycle event payload.</summary>
public sealed record SubAgentEventPayload(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("subAgentId")] string SubAgentId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("archetype")] string Archetype,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("turnsUsed")] int TurnsUsed,
    [property: JsonPropertyName("resultSummary")] string? ResultSummary,
    [property: JsonPropertyName("timedOut")] bool TimedOut);

// ── Client → Server return types ────────────────────────────────────────

/// <summary>Result returned by <c>SendMessage</c>.</summary>
public sealed record SendMessageResult(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("channelType")] string? ChannelType);

/// <summary>Result returned by <c>SubscribeAll</c>.</summary>
public sealed record SubscribeAllResult(
    [property: JsonPropertyName("sessions")] IReadOnlyList<SessionSummary> Sessions);

/// <summary>Session summary returned in <see cref="SubscribeAllResult"/>.</summary>
public sealed record SessionSummary(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("channelType")] string? ChannelType,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

/// <summary>Result returned by <c>CompactSession</c>.</summary>
public sealed record CompactSessionResult(
    [property: JsonPropertyName("summarized")] int Summarized,
    [property: JsonPropertyName("preserved")] int Preserved,
    [property: JsonPropertyName("tokensBefore")] int TokensBefore,
    [property: JsonPropertyName("tokensAfter")] int TokensAfter);

// ── Client-side history DTOs (matches ChannelHistoryController response) ─────

/// <summary>Response from the channel history REST API.</summary>
public sealed record HistoryResponse(
    [property: JsonPropertyName("messages")] IReadOnlyList<HistoryMessage> Messages,
    [property: JsonPropertyName("nextCursor")] string? NextCursor,
    [property: JsonPropertyName("hasMore")] bool HasMore);

/// <summary>A single message in the history response.</summary>
public sealed record HistoryMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("toolName")] string? ToolName = null,
    [property: JsonPropertyName("toolCallId")] string? ToolCallId = null);
