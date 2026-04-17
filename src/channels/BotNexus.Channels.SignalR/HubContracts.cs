using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Channels.SignalR;

#pragma warning disable CS1591 // Hub contract types are self-documenting SignalR DTOs

// ── Hub method return types ──────────────────────────────────────────────

/// <summary>Result returned by <c>SendMessage</c> and <c>SendMessageWithMedia</c>.</summary>
public sealed record SendMessageResult(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("channelType")] string? ChannelType);

/// <summary>Result returned by the (deprecated) <c>JoinSession</c> method.</summary>
public sealed record JoinSessionResult(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("isResumed")] bool IsResumed,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("channelType")] string? ChannelType,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

/// <summary>Result returned by <c>SubscribeAll</c>.</summary>
public sealed record SubscribeAllResult(
    [property: JsonPropertyName("sessions")] IReadOnlyList<SessionSummary> Sessions);

/// <summary>Result returned by <c>CompactSession</c>.</summary>
public sealed record CompactSessionResult(
    [property: JsonPropertyName("summarized")] int Summarized,
    [property: JsonPropertyName("preserved")] int Preserved,
    [property: JsonPropertyName("tokensBefore")] int TokensBefore,
    [property: JsonPropertyName("tokensAfter")] int TokensAfter);

// ── Server → Client event payloads ──────────────────────────────────────

/// <summary>Payload sent via the <c>Connected</c> client method on hub connect.</summary>
public sealed record ConnectedPayload(
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("agents")] IEnumerable<AgentSummary> Agents,
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

/// <summary>Payload sent via the <c>ContentDelta</c> client method from the channel adapter.</summary>
public sealed record ContentDeltaPayload(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("contentDelta")] string? ContentDelta);

/// <summary>Payload sent via sub-agent lifecycle client methods (Spawned, Completed, Failed, Killed).</summary>
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
