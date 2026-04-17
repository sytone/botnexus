using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Context provided to an isolation strategy when creating an agent handle.
/// Contains everything needed to initialize an agent in any execution environment.
/// </summary>
public sealed record AgentExecutionContext
{
    /// <summary>The session this execution is bound to.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Conversation history to restore, if resuming a session.</summary>
    public IReadOnlyList<SessionEntry> History { get; init; } = [];

    /// <summary>Extensible execution parameters.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } =
        new Dictionary<string, object?>();
}

/// <summary>
/// Response from an agent after processing a prompt.
/// </summary>
public sealed record AgentResponse
{
    /// <summary>The full response content.</summary>
    public required string Content { get; init; }

    /// <summary>Token usage for this response, if available.</summary>
    public AgentResponseUsage? Usage { get; init; }

    /// <summary>Whether the agent wants to continue with tool results.</summary>
    public bool RequiresFollowUp { get; init; }

    /// <summary>Any tool calls the agent made during processing.</summary>
    public IReadOnlyList<AgentToolCallInfo> ToolCalls { get; init; } = [];
}

/// <summary>
/// Token usage information for an agent response.
/// </summary>
public sealed record AgentResponseUsage(int? InputTokens = null, int? OutputTokens = null);

/// <summary>
/// Information about a tool call made during agent execution.
/// </summary>
public sealed record AgentToolCallInfo(string ToolCallId, string ToolName, bool IsError);

/// <summary>
/// A streaming event from an agent, emitted during real-time interaction.
/// Maps to the AgentEvent system in AgentCore but at the Gateway level.
/// </summary>
public sealed record AgentStreamEvent
{
    /// <summary>The type of stream event.</summary>
    public required AgentStreamEventType Type { get; init; }

    /// <summary>Incremental content delta (for <see cref="AgentStreamEventType.ContentDelta"/>).</summary>
    public string? ContentDelta { get; init; }

    /// <summary>Incremental thinking delta (for <see cref="AgentStreamEventType.ThinkingDelta"/>).</summary>
    public string? ThinkingContent { get; init; }

    /// <summary>Tool call identifier (for tool-related events).</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Tool name (for tool-related events).</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool arguments (for <see cref="AgentStreamEventType.ToolStart"/>).</summary>
    public IReadOnlyDictionary<string, object?>? ToolArgs { get; init; }

    /// <summary>Tool result content (for <see cref="AgentStreamEventType.ToolEnd"/>).</summary>
    public string? ToolResult { get; init; }

    /// <summary>Whether the tool call errored (for <see cref="AgentStreamEventType.ToolEnd"/>).</summary>
    public bool? ToolIsError { get; init; }

    /// <summary>Error message (for <see cref="AgentStreamEventType.Error"/>).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Token usage (for <see cref="AgentStreamEventType.MessageEnd"/>).</summary>
    public AgentResponseUsage? Usage { get; init; }

    /// <summary>Message identifier for correlation.</summary>
    public string? MessageId { get; init; }

    /// <summary>Session identifier for client-side routing verification.</summary>
    public SessionId? SessionId { get; init; }

    /// <summary>Agent identifier for client-side routing when session is not yet registered.</summary>
    public AgentId? AgentId { get; init; }

    /// <summary>When this event was emitted.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Types of streaming events from an agent.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentStreamEventType
{
    /// <summary>Agent has started processing.</summary>
    MessageStart,

    /// <summary>Incremental content from the agent.</summary>
    ContentDelta,

    /// <summary>Incremental thinking content from the agent.</summary>
    ThinkingDelta,

    /// <summary>A tool execution has started.</summary>
    ToolStart,

    /// <summary>A tool execution has completed.</summary>
    ToolEnd,

    /// <summary>Agent has finished processing the current message.</summary>
    MessageEnd,

    /// <summary>An error occurred during processing.</summary>
    Error
}
