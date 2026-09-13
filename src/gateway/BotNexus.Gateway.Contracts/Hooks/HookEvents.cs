using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Hooks;

// ── Diagnostic execution origin ─────────────────────────────────────

/// <summary>Whether diagnostic provenance describes a real execution or a descriptor-only build.</summary>
public enum DiagnosticOriginKind
{
    /// <summary>No runtime execution exists; execution-scoped identifiers are deliberately absent.</summary>
    DescriptorOnly,

    /// <summary>The origin was populated from authoritative runtime execution state.</summary>
    Execution
}

/// <summary>The operation that caused a diagnostic-producing hook to run.</summary>
public enum DiagnosticTrigger
{
    /// <summary>A prompt was being assembled; this does not imply a skill or tool invocation.</summary>
    PromptConstruction,

    /// <summary>An actual model-issued tool call reached the invocation hook.</summary>
    ToolInvocation
}

/// <summary>
/// Typed provenance carried by diagnostic-producing hooks. Nullable fields mean the producing
/// runtime did not have authoritative evidence for that value; consumers must never fill gaps by
/// temporal correlation or inference.
/// </summary>
/// <param name="Kind">Whether this came from a real execution or a descriptor-only build.</param>
/// <param name="Trigger">The operation that caused the hook. Prompt construction is not invocation.</param>
/// <param name="AgentId">Agent from the descriptor or invocation context; always authoritative.</param>
/// <param name="ConversationId">Conversation resolved through the session/conversation stores, when available.</param>
/// <param name="SessionId">Session supplied by <see cref="AgentExecutionContext"/>, when an execution exists.</param>
/// <param name="RunId">Persisted execution/run identity, only when the caller supplies one.</param>
/// <param name="ToolCallId">Actual model tool-call identity; present only for <see cref="DiagnosticTrigger.ToolInvocation"/>.</param>
/// <param name="Channel">Channel supplied by the resolved execution/session metadata, when available.</param>
/// <param name="SourceComponent">Component that captured the origin, supplied by that producer.</param>
/// <param name="Category">Stable diagnostic category supplied by that producer.</param>
/// <param name="TraceId">W3C trace id from <see cref="System.Diagnostics.Activity.Current"/>, when recording is active.</param>
/// <param name="SpanId">W3C span id from <see cref="System.Diagnostics.Activity.Current"/>, when recording is active.</param>
/// <param name="CorrelationId">Explicit activity correlation tag, falling back to the W3C trace id.</param>
/// <param name="Timestamp">UTC instant at which the origin was captured.</param>
/// <param name="Host">Host identity observed by the producing process.</param>
/// <param name="InstanceId">Runtime instance identity, only when explicitly supplied.</param>
/// <param name="InitiatorId">Conversation initiator from persisted conversation state, when available.</param>
/// <param name="ParentAgentId">Authoritative parent agent identity for delegated execution, when supplied.</param>
/// <param name="ParentSessionId">Authoritative parent session identity for delegated execution, when supplied.</param>
public sealed record DiagnosticExecutionOrigin(
    DiagnosticOriginKind Kind,
    DiagnosticTrigger Trigger,
    AgentId AgentId,
    ConversationId? ConversationId = null,
    SessionId? SessionId = null,
    RunId? RunId = null,
    string? ToolCallId = null,
    string? Channel = null,
    string? SourceComponent = null,
    string? Category = null,
    string? TraceId = null,
    string? SpanId = null,
    string? CorrelationId = null,
    DateTimeOffset Timestamp = default,
    string? Host = null,
    string? InstanceId = null,
    CitizenId? InitiatorId = null,
    AgentId? ParentAgentId = null,
    SessionId? ParentSessionId = null)
{
    /// <summary>Creates explicit non-execution provenance for descriptor-only prompt rendering.</summary>
    public static DiagnosticExecutionOrigin ForDescriptor(AgentId agentId) =>
        Capture(
            DiagnosticOriginKind.DescriptorOnly,
            DiagnosticTrigger.PromptConstruction,
            agentId,
            sourceComponent: "WorkspaceContextBuilder",
            category: "prompt-hook");

    /// <summary>Creates prompt-construction provenance from already-resolved runtime identities.</summary>
    public static DiagnosticExecutionOrigin ForPromptConstruction(
        AgentId agentId,
        SessionId sessionId,
        ConversationId? conversationId,
        string? channel,
        RunId? runId = null,
        CitizenId? initiatorId = null,
        AgentId? parentAgentId = null,
        SessionId? parentSessionId = null,
        string? instanceId = null,
        string sourceComponent = "WorkspaceContextBuilder",
        string category = "prompt-hook") =>
        Capture(
            DiagnosticOriginKind.Execution,
            DiagnosticTrigger.PromptConstruction,
            agentId,
            conversationId,
            sessionId,
            runId,
            toolCallId: null,
            channel,
            initiatorId,
            parentAgentId,
            parentSessionId,
            instanceId,
            sourceComponent,
            category);

    /// <summary>Creates invocation provenance and requires the actual model tool-call identity.</summary>
    public static DiagnosticExecutionOrigin ForToolInvocation(
        AgentId agentId,
        SessionId? sessionId,
        ConversationId? conversationId,
        string toolCallId,
        string? channel,
        RunId? runId = null,
        CitizenId? initiatorId = null,
        AgentId? parentAgentId = null,
        SessionId? parentSessionId = null,
        string? instanceId = null,
        string sourceComponent = "InProcessIsolationStrategy",
        string category = "tool-hook")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        return Capture(
            DiagnosticOriginKind.Execution,
            DiagnosticTrigger.ToolInvocation,
            agentId,
            conversationId,
            sessionId,
            runId,
            toolCallId,
            channel,
            initiatorId,
            parentAgentId,
            parentSessionId,
            instanceId,
            sourceComponent,
            category);
    }

    private static DiagnosticExecutionOrigin Capture(
        DiagnosticOriginKind kind,
        DiagnosticTrigger trigger,
        AgentId agentId,
        ConversationId? conversationId = null,
        SessionId? sessionId = null,
        RunId? runId = null,
        string? toolCallId = null,
        string? channel = null,
        CitizenId? initiatorId = null,
        AgentId? parentAgentId = null,
        SessionId? parentSessionId = null,
        string? instanceId = null,
        string? sourceComponent = null,
        string? category = null)
    {
        var activity = System.Diagnostics.Activity.Current;
        var traceId = activity is null ? null : activity.TraceId.ToString();
        var correlationId = activity?.GetTagItem("botnexus.correlation.id")?.ToString() ?? traceId;
        return new DiagnosticExecutionOrigin(
            kind,
            trigger,
            agentId,
            conversationId,
            sessionId,
            runId,
            toolCallId,
            string.IsNullOrWhiteSpace(channel) ? null : channel,
            string.IsNullOrWhiteSpace(sourceComponent) ? null : sourceComponent,
            string.IsNullOrWhiteSpace(category) ? null : category,
            traceId,
            activity is null ? null : activity.SpanId.ToString(),
            correlationId,
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            instanceId,
            initiatorId,
            parentAgentId,
            parentSessionId);
    }
}

// ── Before prompt build ──────────────────────────────────────────────

/// <summary>
/// Event raised before the system prompt is assembled for an agent invocation.
/// Gateway hook handlers receive this to inject context into the prompt.
/// </summary>
/// <param name="AgentId">The agent being invoked.</param>
/// <param name="Descriptor">The agent descriptor for the invoked agent. Use this instead of resolving from IAgentRegistry — hook handlers may hold stale DI references.</param>
/// <param name="CurrentPrompt">The current system prompt text before modifications.</param>
/// <param name="Messages">The conversation history being sent to the LLM.</param>
/// <param name="Origin">Authoritative execution provenance, or explicit descriptor-only provenance.</param>
public sealed record BeforePromptBuildEvent(
    AgentId AgentId,
    AgentDescriptor Descriptor,
    string CurrentPrompt,
    IReadOnlyList<object> Messages,
    DiagnosticExecutionOrigin Origin)
{
    /// <summary>Compatibility constructor for direct callers that have no execution context.</summary>
    public BeforePromptBuildEvent(
        AgentId agentId,
        AgentDescriptor descriptor,
        string currentPrompt,
        IReadOnlyList<object> messages)
        : this(agentId, descriptor, currentPrompt, messages, DiagnosticExecutionOrigin.ForDescriptor(agentId))
    {
    }
}

/// <summary>
/// Result returned by a gateway hook handler after inspecting <see cref="BeforePromptBuildEvent"/>.
/// Allows prepending or appending context to the system prompt without replacing it.
/// </summary>
public sealed record BeforePromptBuildResult
{
    /// <summary>Text to prepend before the existing system prompt.</summary>
    public string? PrependSystemContext { get; init; }

    /// <summary>Text to append after the existing system prompt.</summary>
    public string? AppendSystemContext { get; init; }
}

// ── Before context files are assembled ──────────────────────────────

/// <summary>
/// A workspace context file destined for the system prompt, in the shape hook handlers exchange
/// it. Deliberately a Contracts-level primitive (path + content) rather than the gateway's own
/// <c>ContextFile</c> so extension assemblies can contribute context without referencing the
/// prompt-assembly internals.
/// </summary>
/// <param name="Path">Workspace-relative path, used as the prompt heading and the identity key.</param>
/// <param name="Content">The file body to inject.</param>
public sealed record PromptContextFile(string Path, string Content);

/// <summary>
/// Event raised after workspace context files have been loaded but <em>before</em> the system
/// prompt is assembled from them (issue #2846). Handlers may contribute additional context files.
/// </summary>
/// <remarks>
/// This is the seam that makes the owner-private exclusion enforceable. The exclusion is applied
/// to the file set <em>after</em> this event is dispatched and <em>before</em> assembly, so a hook
/// cannot reintroduce <c>MEMORY.md</c> or <c>USER.md</c> into a shared conversation, and the
/// private content is never materialised into prompt text in the first place. Contrast
/// <see cref="BeforePromptBuildEvent"/>, which operates on the already-assembled string and is
/// therefore the wrong place to enforce a content boundary.
/// </remarks>
/// <param name="AgentId">The agent being invoked.</param>
/// <param name="Descriptor">The agent descriptor for the invoked agent.</param>
/// <param name="Scope">Whether the conversation is owner-private or shared.</param>
/// <param name="ContextFiles">The context files loaded so far, in prompt order.</param>
public sealed record BeforeContextFilesBuildEvent(
    AgentId AgentId,
    AgentDescriptor Descriptor,
    BotNexus.Gateway.Abstractions.Agents.ConversationScope Scope,
    IReadOnlyList<PromptContextFile> ContextFiles);

/// <summary>
/// Result returned by a handler of <see cref="BeforeContextFilesBuildEvent"/>.
/// </summary>
public sealed record BeforeContextFilesBuildResult
{
    /// <summary>
    /// Context files to append to the set. Additions are subject to the same owner-private
    /// exclusion as the loaded set — a handler cannot use this to smuggle private content into a
    /// shared conversation.
    /// </summary>
    public IReadOnlyList<PromptContextFile> AdditionalContextFiles { get; init; } = [];
}

// ── Before tool call ─────────────────────────────────────────────────

/// <summary>
/// Event raised before a tool call is executed by the agent runtime.
/// Gateway hook handlers receive this to enforce policies, modify arguments, or deny execution.
/// </summary>
/// <param name="AgentId">The agent making the tool call.</param>
/// <param name="ToolName">Name of the tool being called.</param>
/// <param name="ToolCallId">Unique identifier for this tool invocation.</param>
/// <param name="Arguments">The arguments the LLM provided for the tool call.</param>
public sealed record BeforeToolCallEvent(
    AgentId AgentId,
    string ToolName,
    string ToolCallId,
    IReadOnlyDictionary<string, object?> Arguments,
    DiagnosticExecutionOrigin Origin)
{
    /// <summary>Compatibility constructor for direct tests and extension callers.</summary>
    public BeforeToolCallEvent(
        AgentId agentId,
        string toolName,
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments)
        : this(
            agentId,
            toolName,
            toolCallId,
            arguments,
            DiagnosticExecutionOrigin.ForToolInvocation(agentId, null, null, toolCallId, null))
    {
    }
}

/// <summary>
/// Result returned by a gateway hook handler after inspecting <see cref="BeforeToolCallEvent"/>.
/// Can deny execution or modify the tool arguments before the tool runs.
/// </summary>
/// <remarks>
/// This is the <b>gateway-level</b> hook result used by extensions and hook handlers.
/// It is translated to the agent-level <c>Agent.Core.Hooks.BeforeToolCallResult</c>
/// by <c>InProcessIsolationStrategy</c> at the boundary. The gateway version adds
/// argument modification capability that the agent-level type does not have.
/// </remarks>
public sealed record BeforeToolCallResult
{
    /// <summary>When <c>true</c>, the tool call is blocked and an error result is returned to the LLM.</summary>
    public bool Denied { get; init; }

    /// <summary>Human-readable reason for denial. Sent to the LLM as the tool error message when <see cref="Denied"/> is <c>true</c>.</summary>
    public string? DenyReason { get; init; }

    /// <summary>
    /// Replacement arguments for the tool call. When non-null, these replace the original
    /// LLM-provided arguments before the tool executes. Use for argument sanitization or enrichment.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ModifiedArguments { get; init; }
}

// ── After tool call ──────────────────────────────────────────────────

/// <summary>
/// Event raised after a tool call completes. Gateway hook handlers receive this
/// for logging, auditing, or post-processing tool results.
/// </summary>
/// <param name="AgentId">The agent that made the tool call.</param>
/// <param name="ToolName">Name of the tool that was called.</param>
/// <param name="ToolCallId">Unique identifier for this tool invocation.</param>
/// <param name="Result">The tool's text result (may be null for tools that return structured data).</param>
/// <param name="IsError">Whether the tool execution reported an error.</param>
public sealed record AfterToolCallEvent(
    AgentId AgentId,
    string ToolName,
    string ToolCallId,
    string? Result,
    bool IsError,
    DiagnosticExecutionOrigin Origin)
{
    /// <summary>Compatibility constructor for direct tests and extension callers.</summary>
    public AfterToolCallEvent(
        AgentId agentId,
        string toolName,
        string toolCallId,
        string? result,
        bool isError)
        : this(
            agentId,
            toolName,
            toolCallId,
            result,
            isError,
            DiagnosticExecutionOrigin.ForToolInvocation(agentId, null, null, toolCallId, null))
    {
    }
}

/// <summary>
/// Result returned by a gateway hook handler after inspecting <see cref="AfterToolCallEvent"/>.
/// Currently a marker type — the gateway hook system does not support post-execution
/// result transformation. Use <c>Agent.Core.Hooks.AfterToolCallResult</c> at the
/// agent level for result overrides (content replacement, error flag changes).
/// </summary>
public sealed record AfterToolCallResult;
