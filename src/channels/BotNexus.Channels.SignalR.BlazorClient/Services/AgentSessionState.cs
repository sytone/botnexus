namespace BotNexus.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Independent state for one agent's session. No global shared state —
/// each chat panel is scoped to exactly one <see cref="AgentSessionState"/>.
/// </summary>
public sealed class AgentSessionState
{
    /// <summary>The agent's unique identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>Human-friendly display name for the agent.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Active session ID (set after first message exchange).</summary>
    public string? SessionId { get; set; }

    /// <summary>Channel type for this session.</summary>
    public string? ChannelType { get; set; }

    /// <summary>All messages in this session, in chronological order.</summary>
    public List<ChatMessage> Messages { get; } = [];

    /// <summary>Whether the agent is currently streaming a response.</summary>
    public bool IsStreaming { get; set; }

    /// <summary>Buffer for the in-progress streaming response.</summary>
    public string CurrentStreamBuffer { get; set; } = "";

    /// <summary>Whether the hub connection is active.</summary>
    public bool IsConnected { get; set; }

    /// <summary>Count of unread messages while this agent's tab is not active.</summary>
    public int UnreadCount { get; set; }

    /// <summary>Whether history has been loaded from the REST API for this agent.</summary>
    public bool HistoryLoaded { get; set; }

    /// <summary>Whether a history fetch is currently in-flight.</summary>
    public bool IsLoadingHistory { get; set; }

    /// <summary>In-progress tool calls keyed by tool-call ID.</summary>
    public Dictionary<string, ActiveToolCall> ActiveToolCalls { get; } = new();
}

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

    /// <summary>CSS class derived from the message role.</summary>
    public string CssClass => Role.ToLowerInvariant();
}
