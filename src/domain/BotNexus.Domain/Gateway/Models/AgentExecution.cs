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
public sealed record AgentResponseUsage(
    int? InputTokens = null,
    int? OutputTokens = null,
    int? CacheRead = null,
    int? CacheWrite = null);
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
    /// <summary>Incremental content delta (for <see cref=`AgentStreamEventType.ContentDelta`/>).</summary>
    public string? ContentDelta { get; init; }
    /// <summary>Incremental thinking delta (for <see cref=`AgentStreamEventType.ThinkingDelta`/>).</summary>
    public string? ThinkingContent { get; init; }
    /// <summary>Tool call identifier (for tool-related events).</summary>
    public string? ToolCallId { get; init; }
    /// <summary>Tool name (for tool-related events).</summary>
    public string? ToolName { get; init; }
    /// <summary>Tool arguments (for <see cref=`AgentStreamEventType.ToolStart`/>).</summary>
    public IReadOnlyDictionary<string, object?>? ToolArgs { get; init; }
    /// <summary>Tool result content (for <see cref=`AgentStreamEventType.ToolEnd`/>).</summary>
    public string? ToolResult { get; init; }
    /// <summary>Whether the tool call errored (for <see cref=`AgentStreamEventType.ToolEnd`/>).</summary>
    public bool? ToolIsError { get; init; }
    /// <summary>Error message (for <see cref=`AgentStreamEventType.Error`/>).</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Token usage (for <see cref=`AgentStreamEventType.MessageEnd`/>).</summary>
    public AgentResponseUsage? Usage { get; init; }
    /// <summary>Message identifier for correlation.</summary>
    public string? MessageId { get; init; }
    /// <summary>Session identifier for client-side routing verification.</summary>
    public SessionId? SessionId { get; init; }
    /// <summary>Agent identifier for client-side routing when session is not yet registered.</summary>
    public AgentId? AgentId { get; init; }
    /// <summary>
    /// Conversation identifier — the persistent user-facing thread. Stable across compaction:
    /// when a session is compacted and a new session is created within the same conversation,
    /// stream events emitted by the new session carry the same <c>ConversationId</c>. This is
    /// the primary client-side routing key; <see cref="SessionId"/> remains useful for
    /// provenance and per-session UI affordances.
    /// </summary>
    public ConversationId? ConversationId { get; init; }
    /// <summary>When this event was emitted.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>
    /// Structured payload for <see cref=`AgentStreamEventType.UserInputRequired`/> events.
    /// Present when an agent is blocked waiting for user input mid-turn.
    /// </summary>
    public AskUserRequest? UserInputRequest { get; init; }
}
/// <summary>
/// Types of streaming events from an agent.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentStreamEventType
{
    /// <summary>
    /// The agent run loop has started. Emitted once at the very beginning of a run, before any
    /// turn, message, or tool events. Pairs with <see cref="RunEnded"/> to bracket the entire
    /// loop (all turns, tool cycles, and follow-up continuations).
    /// </summary>
    /// <remarks>
    /// This is the authoritative "agent is busy" signal for clients. Unlike <see cref="MessageStart"/>
    /// and <see cref="ToolStart"/>, which only cover individual steps, <c>RunStarted</c>/<c>RunEnded</c>
    /// stay asserted across the inter-step gaps (message-end -> tool-start, tool-end -> tool-start,
    /// tool-end -> next message-start) where the loop is still running but no single step is active.
    /// </remarks>
    RunStarted,

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
    Error,
    /// <summary>A turn (LLM call + tool cycle) has completed. Used for mid-run persistence checkpoints.</summary>
    TurnEnd,
    /// <summary>Agent execution is paused while awaiting interactive user input.</summary>
    UserInputRequired,
    /// <summary>The gateway restarted mid-turn; the interrupted session has been flagged and the user notified.</summary>
    TurnInterrupted,

    /// <summary>
    /// The agent run loop has fully settled. Emitted exactly once when the entire loop completes,
    /// aborts, or errors out — after the final turn, the last tool result, and any follow-up
    /// continuations. Pairs with <see cref="RunStarted"/>.
    /// </summary>
    /// <remarks>
    /// Clients should treat the agent as idle only after this event. Because it brackets the whole
    /// loop, it is the reliable signal for steer/follow-up/stop control visibility — it does not
    /// flicker between turns or tools the way <see cref="MessageEnd"/>/<see cref="ToolEnd"/> do.
    /// </remarks>
    RunEnded
}
