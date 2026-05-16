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

/// <summary>
/// Represents a pending <c>ask_user</c> checkpoint that blocks the agent until
/// the user submits an answer or cancels from the chat panel.
/// </summary>
public record AskUserPromptState
{
    /// <summary>Unique request identifier used when posting the response to the hub.</summary>
    public required string RequestId { get; init; }

    /// <summary>Conversation that owns this prompt and can satisfy it.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Prompt text rendered inline in the chat stream.</summary>
    public required string Prompt { get; init; }

    /// <summary>Input mode requested by the tool (FreeForm, SingleChoice, MultipleChoice, ChoiceOrFreeForm).</summary>
    public required string InputType { get; init; }

    /// <summary>Optional structured choices for choice-based prompts.</summary>
    public IReadOnlyList<AskUserChoiceState>? Choices { get; init; }

    /// <summary>Whether the user may select multiple predefined choices.</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>Whether a custom free-form response is allowed.</summary>
    public bool AllowFreeForm { get; init; }

    /// <summary>Absolute expiration timestamp if the prompt has a timeout.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>True while a response is being submitted to prevent duplicate sends.</summary>
    public bool IsSubmitting { get; set; }
}

/// <summary>
/// Single choice item shown in an inline <c>ask_user</c> prompt.
/// </summary>
/// <param name="Value">Stable value returned to the tool when selected.</param>
/// <param name="Label">Display label shown to the user.</param>
/// <param name="Description">Optional helper text describing the option.</param>
public record AskUserChoiceState(string Value, string Label, string? Description);

/// <summary>
/// User response payload emitted by the inline ask-user component and sent to
/// the gateway hub to satisfy a pending request.
/// </summary>
/// <param name="FreeFormText">Optional free-form text response.</param>
/// <param name="SelectedValues">Optional set of selected choice values.</param>
/// <param name="Cancelled">True when the user cancelled instead of submitting an answer.</param>
public record AskUserPromptSubmission(string? FreeFormText, string[]? SelectedValues, bool Cancelled);
