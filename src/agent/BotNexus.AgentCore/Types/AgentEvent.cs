using BotNexus.Core.Models;

namespace BotNexus.AgentCore.Types;

/// <summary>
/// Base event contract for the pi-mono style agent lifecycle event stream.
/// </summary>
/// <param name="Type">The event discriminator.</param>
/// <param name="Timestamp">The event timestamp.</param>
public abstract record AgentEvent(AgentEventType Type, DateTimeOffset Timestamp);

/// <summary>
/// Raised when an agent run starts.
/// </summary>
/// <param name="Timestamp">The event timestamp.</param>
public sealed record AgentStartEvent(DateTimeOffset Timestamp) : AgentEvent(AgentEventType.AgentStart, Timestamp);

/// <summary>
/// Raised when an agent run ends.
/// </summary>
/// <param name="Messages">The final message timeline.</param>
/// <param name="Timestamp">The event timestamp.</param>
public sealed record AgentEndEvent(IReadOnlyList<AgentMessage> Messages, DateTimeOffset Timestamp)
    : AgentEvent(AgentEventType.AgentEnd, Timestamp);

/// <summary>
/// Raised when a new turn starts.
/// </summary>
/// <param name="Timestamp">The event timestamp.</param>
public sealed record TurnStartEvent(DateTimeOffset Timestamp) : AgentEvent(AgentEventType.TurnStart, Timestamp);

/// <summary>
/// Raised when a turn completes.
/// </summary>
/// <param name="Message">The assistant message produced for the turn.</param>
/// <param name="ToolResults">The tool results produced in the turn.</param>
/// <param name="Timestamp">The event timestamp.</param>
public sealed record TurnEndEvent(
    AssistantAgentMessage Message,
    IReadOnlyList<ToolResultAgentMessage> ToolResults,
    DateTimeOffset Timestamp) : AgentEvent(AgentEventType.TurnEnd, Timestamp);

/// <summary>
/// Raised when assistant message generation starts.
/// </summary>
/// <param name="Message">The in-progress assistant message.</param>
/// <param name="Timestamp">The event timestamp.</param>
public sealed record MessageStartEvent(AssistantAgentMessage Message, DateTimeOffset Timestamp)
    : AgentEvent(AgentEventType.MessageStart, Timestamp);

/// <summary>
/// Raised for streaming updates while generating an assistant message.
/// </summary>
/// <param name="Message">The current assistant message snapshot.</param>
/// <param name="ContentDelta">The streamed content delta.</param>
/// <param name="ToolCallId">The active streamed tool call identifier.</param>
/// <param name="ToolName">The active streamed tool name.</param>
/// <param name="ArgumentsDelta">The streamed tool arguments delta.</param>
/// <param name="FinishReason">The optional streamed finish reason.</param>
/// <param name="InputTokens">The optional streamed input token count.</param>
/// <param name="OutputTokens">The optional streamed output token count.</param>
/// <param name="Timestamp">The event timestamp.</param>
public sealed record MessageUpdateEvent(
    AssistantAgentMessage Message,
    string? ContentDelta,
    string? ToolCallId,
    string? ToolName,
    string? ArgumentsDelta,
    FinishReason? FinishReason,
    int? InputTokens,
    int? OutputTokens,
    DateTimeOffset Timestamp) : AgentEvent(AgentEventType.MessageUpdate, Timestamp);

/// <summary>
/// Raised when assistant message generation ends.
/// </summary>
/// <param name="Message">The completed assistant message.</param>
/// <param name="Timestamp">The event timestamp.</param>
public sealed record MessageEndEvent(AssistantAgentMessage Message, DateTimeOffset Timestamp)
    : AgentEvent(AgentEventType.MessageEnd, Timestamp);

/// <summary>
/// Raised when a tool execution starts.
/// </summary>
/// <param name="ToolCallId">The tool call identifier.</param>
/// <param name="ToolName">The tool name.</param>
/// <param name="Args">The validated tool arguments.</param>
/// <param name="Timestamp">The event timestamp.</param>
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
/// <param name="PartialResult">A partial tool result snapshot.</param>
/// <param name="Timestamp">The event timestamp.</param>
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
/// <param name="IsError">Indicates whether the tool failed.</param>
/// <param name="Timestamp">The event timestamp.</param>
public sealed record ToolExecutionEndEvent(
    string ToolCallId,
    string ToolName,
    AgentToolResult Result,
    bool IsError,
    DateTimeOffset Timestamp) : AgentEvent(AgentEventType.ToolExecutionEnd, Timestamp);
