using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Event protocol for assistant message streaming.
/// Port of pi-mono's AssistantMessageEvent discriminated union.
/// </summary>
public abstract record AssistantMessageEvent(string Type);

/// <summary>
/// Represents start event.
/// </summary>
public sealed record StartEvent(
    AssistantMessage Partial
) : AssistantMessageEvent("start");

/// <summary>
/// Represents text start event.
/// </summary>
public sealed record TextStartEvent(
    int ContentIndex,
    AssistantMessage Partial
) : AssistantMessageEvent("text_start");

/// <summary>
/// Represents text delta event.
/// </summary>
public sealed record TextDeltaEvent(
    int ContentIndex,
    string Delta,
    AssistantMessage Partial
) : AssistantMessageEvent("text_delta");

/// <summary>
/// Represents text end event.
/// </summary>
public sealed record TextEndEvent(
    int ContentIndex,
    string Content,
    AssistantMessage Partial
) : AssistantMessageEvent("text_end");

/// <summary>
/// Represents thinking start event.
/// </summary>
public sealed record ThinkingStartEvent(
    int ContentIndex,
    AssistantMessage Partial
) : AssistantMessageEvent("thinking_start");

/// <summary>
/// Represents thinking delta event.
/// </summary>
public sealed record ThinkingDeltaEvent(
    int ContentIndex,
    string Delta,
    AssistantMessage Partial
) : AssistantMessageEvent("thinking_delta");

/// <summary>
/// Represents thinking end event.
/// </summary>
public sealed record ThinkingEndEvent(
    int ContentIndex,
    string Content,
    AssistantMessage Partial
) : AssistantMessageEvent("thinking_end");

/// <summary>
/// Represents tool call start event.
/// </summary>
public sealed record ToolCallStartEvent(
    int ContentIndex,
    AssistantMessage Partial
) : AssistantMessageEvent("toolcall_start");

/// <summary>
/// Represents tool call delta event.
/// </summary>
public sealed record ToolCallDeltaEvent(
    int ContentIndex,
    string Delta,
    AssistantMessage Partial
) : AssistantMessageEvent("toolcall_delta");

/// <summary>
/// Represents tool call end event.
/// </summary>
public sealed record ToolCallEndEvent(
    int ContentIndex,
    ToolCallContent ToolCall,
    AssistantMessage Partial
) : AssistantMessageEvent("toolcall_end");

/// <summary>
/// Represents done event.
/// </summary>
public sealed record DoneEvent(
    StopReason Reason,
    AssistantMessage Message
) : AssistantMessageEvent("done");

/// <summary>
/// Represents error event.
/// </summary>
public sealed record ErrorEvent(
    StopReason Reason,
    AssistantMessage Error
) : AssistantMessageEvent("error");
