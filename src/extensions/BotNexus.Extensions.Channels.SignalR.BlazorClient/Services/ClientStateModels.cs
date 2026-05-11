namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Tracks an in-progress tool invocation so we can compute duration on completion.
/// </summary>
public sealed class ActiveToolCall
{
    /// <summary>The tool-call ID from the server event.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>Human-readable tool name.</summary>
    public required string ToolName { get; init; }

    /// <summary>When the tool invocation started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The <see cref="ChatMessage.Id"/> of the ToolStart message so we can update it on ToolEnd.</summary>
    public required string MessageId { get; init; }
}

/// <summary>
/// Tracks a sub-agent spawned by a parent agent.
/// </summary>
public sealed class SubAgentInfo
{
    /// <summary>Unique sub-agent identifier.</summary>
    public required string SubAgentId { get; init; }

    /// <summary>Human-readable name of the sub-agent.</summary>
    public string? Name { get; set; }

    /// <summary>The task assigned to this sub-agent.</summary>
    public string Task { get; set; } = "";

    /// <summary>The parent conversation where this sub-agent was started.</summary>
    public string? OriginConversationId { get; set; }

    /// <summary>Current status: Running, Completed, Failed, Killed.</summary>
    public string Status { get; set; } = "Running";

    /// <summary>When the sub-agent was spawned.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the sub-agent finished (if completed/failed/killed).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Summary of the sub-agent's result.</summary>
    public string? ResultSummary { get; set; }

    /// <summary>Model used by the sub-agent.</summary>
    public string? Model { get; set; }

    /// <summary>Archetype of the sub-agent.</summary>
    public string? Archetype { get; set; }
}

/// <summary>
/// A single chat message in an agent session.
/// </summary>
public sealed record ChatMessage(string Role, string Content, DateTimeOffset Timestamp)
{
    /// <summary>Stable identity for markdown caching and tool-call linking.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Tool name if this is a tool-related message.</summary>
    public string? ToolName { get; init; }

    /// <summary>Server-assigned tool-call identifier for linking start/end events.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Serialized tool arguments (JSON).</summary>
    public string? ToolArgs { get; init; }

    /// <summary>Tool execution result.</summary>
    public string? ToolResult { get; init; }

    /// <summary>Whether this message represents a tool call.</summary>
    public bool IsToolCall { get; init; }

    /// <summary>Whether the tool call ended in error.</summary>
    public bool? ToolIsError { get; init; }

    /// <summary>Elapsed wall-clock time for the tool invocation.</summary>
    public TimeSpan? ToolDuration { get; init; }

    /// <summary>Thinking content attached to this assistant message (from ThinkingDelta events).</summary>
    public string? ThinkingContent { get; init; }

    /// <summary>Message kind: "message" (default) or "boundary" (session divider).</summary>
    public string Kind { get; init; } = "message";

    /// <summary>Human-readable label for session boundary entries.</summary>
    public string? BoundaryLabel { get; init; }

    /// <summary>Session ID encoded in the boundary entry.</summary>
    public string? BoundarySessionId { get; init; }

    /// <summary>Whether this entry is a session boundary divider.</summary>
    public bool IsBoundary => Kind == "boundary";

    /// <summary>CSS class derived from the message role.</summary>
    public string CssClass => Role.ToLowerInvariant();
}
