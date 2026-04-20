namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

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

    /// <summary>Session type — user-agent, agent-subagent, etc. Determines read-only behavior.</summary>
    public string SessionType { get; set; } = "user-agent";

    /// <summary>
    /// Whether this session is read-only. True for sub-agent sessions — users can observe
    /// but cannot send messages.
    /// </summary>
    public bool IsReadOnly => SessionType == "agent-subagent";

    /// <summary>All messages in this session, in chronological order.</summary>
    public List<ChatMessage> Messages { get; } = [];

    /// <summary>Whether the agent is currently streaming a response.</summary>
    public bool IsStreaming { get; set; }

    /// <summary>Buffer for the in-progress streaming response.</summary>
    public string CurrentStreamBuffer { get; set; } = "";

    /// <summary>Buffer for in-progress thinking content during streaming.</summary>
    public string ThinkingBuffer { get; set; } = "";

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

    /// <summary>Sub-agents spawned by this agent, keyed by sub-agent ID.</summary>
    public Dictionary<string, SubAgentInfo> SubAgents { get; } = new();

    /// <summary>Current processing stage description for the status bar (e.g. "Thinking…", "Using tool: grep").</summary>
    public string? ProcessingStage { get; set; }

    /// <summary>Whether tool messages are visible in the chat panel.</summary>
    public bool ShowTools { get; set; } = true;

    /// <summary>Whether thinking blocks are visible in the chat panel.</summary>
    public bool ShowThinking { get; set; } = true;
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

    /// <summary>CSS class derived from the message role.</summary>
    public string CssClass => Role.ToLowerInvariant();
}
