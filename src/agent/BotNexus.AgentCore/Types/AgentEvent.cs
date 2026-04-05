using BotNexus.Providers.Core.Models;

namespace BotNexus.AgentCore.Types;

/// <summary>
/// Base event contract for the pi-mono style agent lifecycle event stream.
/// </summary>
/// <param name="Type">The event discriminator.</param>
/// <param name="Timestamp">The event timestamp (UTC).</param>
/// <remarks>
/// Events are emitted via Agent.Subscribe listeners during agent runs.
/// All events are awaited in listener order before the run proceeds.
/// </remarks>
public abstract record AgentEvent(AgentEventType Type, DateTimeOffset Timestamp);

/// <summary>
/// Raised when an agent run starts.
/// </summary>
/// <param name="Timestamp">The event timestamp.</param>
/// <remarks>
/// The first event in every PromptAsync or ContinueAsync run.
/// Followed by TurnStartEvent.
/// </remarks>
public sealed record AgentStartEvent(DateTimeOffset Timestamp) : AgentEvent(AgentEventType.AgentStart, Timestamp);

/// <summary>
/// Raised when an agent run ends.
/// </summary>
/// <param name="Messages">The new messages produced during this run.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <remarks>
/// <para>
/// The final event for a run. The agent becomes idle after all listeners settle.
/// Messages includes only messages produced during this run.
/// </para>
/// <para>
/// Emitted after all turns and tool executions complete, or after an error/abort.
/// </para>
/// </remarks>
public sealed record AgentEndEvent(IReadOnlyList<AgentMessage> Messages, DateTimeOffset Timestamp)
    : AgentEvent(AgentEventType.AgentEnd, Timestamp);

/// <summary>
/// Raised when a new turn starts.
/// </summary>
/// <param name="Timestamp">The event timestamp.</param>
/// <remarks>
/// Emitted before each LLM call. A run may have multiple turns if steering messages
/// are queued or tool calls require additional LLM invocations.
/// </remarks>
public sealed record TurnStartEvent(DateTimeOffset Timestamp) : AgentEvent(AgentEventType.TurnStart, Timestamp);

/// <summary>
/// Raised when a turn completes.
/// </summary>
/// <param name="Message">The assistant message produced for the turn.</param>
/// <param name="ToolResults">The tool results produced in the turn (empty if no tools were called).</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <remarks>
/// Emitted after the assistant message and all tool results are finalized.
/// Check Message.FinishReason for stop/error/aborted status.
/// </remarks>
public sealed record TurnEndEvent(
    AssistantAgentMessage Message,
    IReadOnlyList<ToolResultAgentMessage> ToolResults,
    DateTimeOffset Timestamp) : AgentEvent(AgentEventType.TurnEnd, Timestamp);

/// <summary>
/// Raised when message processing starts.
/// </summary>
/// <param name="Message">The message that started processing.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <remarks>
/// Followed by zero or more MessageUpdateEvents, then MessageEndEvent.
/// Use for UI streaming start markers.
/// </remarks>
public sealed record MessageStartEvent(AgentMessage Message, DateTimeOffset Timestamp)
    : AgentEvent(AgentEventType.MessageStart, Timestamp);

/// <summary>
/// Raised for streaming updates while generating an assistant message.
/// </summary>
/// <param name="Message">The current assistant message snapshot (accumulated content).</param>
/// <param name="ContentDelta">The streamed content delta (new text since last update).</param>
/// <param name="IsThinking">Indicates whether ContentDelta is thinking content rather than response text.</param>
/// <param name="ToolCallId">The active streamed tool call identifier when streaming tool calls.</param>
/// <param name="ToolName">The active streamed tool name when streaming tool calls.</param>
/// <param name="ArgumentsDelta">The streamed tool arguments delta (new JSON text since last update).</param>
/// <param name="FinishReason">The optional streamed finish reason (set when streaming completes).</param>
/// <param name="InputTokens">The optional streamed input token count.</param>
/// <param name="OutputTokens">The optional streamed output token count.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <remarks>
/// Emitted for each chunk during streaming. Message contains the full accumulated content.
/// Use ContentDelta or ArgumentsDelta to display incremental updates in real-time.
/// </remarks>
public sealed record MessageUpdateEvent(
    AssistantAgentMessage Message,
    string? ContentDelta,
    bool IsThinking,
    string? ToolCallId,
    string? ToolName,
    string? ArgumentsDelta,
    StopReason? FinishReason,
    int? InputTokens,
    int? OutputTokens,
    DateTimeOffset Timestamp) : AgentEvent(AgentEventType.MessageUpdate, Timestamp);

/// <summary>
/// Raised when message processing ends.
/// </summary>
/// <param name="Message">The completed message.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <remarks>
/// The final message event for a turn. Message contains the complete content and tool calls.
/// Followed by ToolExecutionStartEvent if Message.ToolCalls is non-empty.
/// </remarks>
public sealed record MessageEndEvent(AgentMessage Message, DateTimeOffset Timestamp)
    : AgentEvent(AgentEventType.MessageEnd, Timestamp);

/// <summary>
/// Raised when a tool execution starts.
/// </summary>
/// <param name="ToolCallId">The tool call identifier.</param>
/// <param name="ToolName">The tool name.</param>
/// <param name="Args">The raw tool arguments (before PrepareArgumentsAsync validation).</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <remarks>
/// Emitted before argument validation and before-hooks run, as the first event for each tool call.
/// For parallel execution, all ToolExecutionStartEvents are emitted before any tool executes.
/// </remarks>
public sealed record ToolExecutionStartEvent(
    string ToolCallId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Args,
    DateTimeOffset Timestamp) : AgentEvent(AgentEventType.ToolExecutionStart, Timestamp);

/// <summary>
/// Raised for incremental updates during tool execution.
/// </summary>
/// <param name="ToolCallId">The tool call identifier.</param>
/// <param name="ToolName">The tool name.</param>
/// <param name="Args">The validated tool arguments.</param>
/// <param name="PartialResult">A partial tool result snapshot (optional).</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <remarks>
/// Reserved for future use. Tools may emit progress updates during long-running operations.
/// </remarks>
public sealed record ToolExecutionUpdateEvent(
    string ToolCallId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Args,
    AgentToolResult? PartialResult,
    DateTimeOffset Timestamp) : AgentEvent(AgentEventType.ToolExecutionUpdate, Timestamp);

/// <summary>
/// Raised when a tool execution finishes.
/// </summary>
/// <param name="ToolCallId">The tool call identifier.</param>
/// <param name="ToolName">The tool name.</param>
/// <param name="Result">The completed tool result.</param>
/// <param name="IsError">Indicates whether the tool failed (exception, validation error, or hook block).</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <remarks>
/// Emitted after ExecuteAsync completes (or fails) and after-hooks run.
/// For parallel execution, ToolExecutionEndEvents are emitted in original assistant message order.
/// </remarks>
public sealed record ToolExecutionEndEvent(
    string ToolCallId,
    string ToolName,
    AgentToolResult Result,
    bool IsError,
    DateTimeOffset Timestamp) : AgentEvent(AgentEventType.ToolExecutionEnd, Timestamp);
