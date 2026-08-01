using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Streaming;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// The single, shared shape of one executed tool call in an agent run timeline (issue #2613).
/// </summary>
/// <remarks>
/// <para>
/// Before #2613 the blocking <c>PromptAsync</c> boundary and the interactive streaming boundary
/// each carried their own partial view of tool activity, so a cron / soul / heartbeat / sub-agent
/// run lost timestamps and correlation that an interactive run kept. This record is the ONE type
/// both paths project into, so the timeline survives either boundary with the same fidelity.
/// </para>
/// <para>
/// Instances are only ever produced by <see cref="ToolInvocationRecordPolicy.Create"/>. The
/// constructor is deliberately non-public so no caller can assemble a record whose arguments or
/// result bypassed the redaction + truncation policy (AC4).
/// </para>
/// </remarks>
public sealed record ToolInvocationRecord
{
    /// <summary>
    /// Non-public so the policy is the only production route to a record (AC4). See
    /// <see cref="ToolInvocationRecordPolicy.Create"/>.
    /// </summary>
    internal ToolInvocationRecord()
    {
    }

    /// <summary>Zero-based position of this call in the run's execution order.</summary>
    public int OrderIndex { get; init; }

    /// <summary>The provider tool-call correlation id (matches the model's tool-use id).</summary>
    public required string ToolCallId { get; init; }

    /// <summary>The invoked tool name.</summary>
    public required string ToolName { get; init; }

    /// <summary>Serialized JSON arguments, already redacted and truncated; null when unavailable.</summary>
    public string? Arguments { get; init; }

    /// <summary>Tool result content, already redacted and truncated; null when there was none.</summary>
    public string? ResultContent { get; init; }

    /// <summary>True when the tool execution failed.</summary>
    public bool IsError { get; init; }

    /// <summary>True when the call was requested but never produced a result (interrupted run).</summary>
    public bool IsIncomplete { get; init; }

    /// <summary>When the call was issued, when the producing boundary observed it.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the result landed; null for an incomplete call.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The id of the parent tool call when this call was nested under another; else null.</summary>
    public string? ParentToolCallId { get; init; }
}

/// <summary>
/// The single truncation + redaction policy applied at the <see cref="ToolInvocationRecord"/>
/// boundary (issue #2613, AC4).
/// </summary>
/// <remarks>
/// Both the blocking and the streaming producer go through this type, so there is exactly one
/// place where a byte budget and a secret sweep are decided. Truncation reuses the existing
/// rune-safe helper <see cref="StreamingSessionHelper.TruncateToolResult"/> (#1598) rather than
/// introducing a second truncation implementation; redaction reuses the existing
/// <see cref="SecretRedactor"/> pattern set rather than a second pattern list.
/// </remarks>
public sealed class ToolInvocationRecordPolicy
{
    /// <summary>
    /// Default byte budget for a record's arguments/result. Matches the platform default cap on a
    /// persisted tool result (16 KiB) so a record never carries more than history would.
    /// </summary>
    public const int DefaultMaxBytes = 16384;

    private readonly ISecretRedactor _redactor;

    /// <summary>
    /// Creates a policy with explicit byte budgets. A non-positive budget disables the cap for
    /// that field.
    /// </summary>
    /// <param name="maxArgumentBytes">UTF-8 byte budget for serialized arguments.</param>
    /// <param name="maxResultBytes">UTF-8 byte budget for result content.</param>
    /// <param name="redactor">Optional redactor override; defaults to the shared pattern set.</param>
    public ToolInvocationRecordPolicy(
        int maxArgumentBytes = DefaultMaxBytes,
        int maxResultBytes = DefaultMaxBytes,
        ISecretRedactor? redactor = null)
    {
        MaxArgumentBytes = maxArgumentBytes;
        MaxResultBytes = maxResultBytes;
        _redactor = redactor ?? new SecretRedactor();
    }

    /// <summary>The shared default policy used by both the blocking and the streaming producer.</summary>
    public static ToolInvocationRecordPolicy Default { get; } = new();

    /// <summary>UTF-8 byte budget applied to <see cref="ToolInvocationRecord.Arguments"/>.</summary>
    public int MaxArgumentBytes { get; }

    /// <summary>UTF-8 byte budget applied to <see cref="ToolInvocationRecord.ResultContent"/>.</summary>
    public int MaxResultBytes { get; }

    /// <summary>
    /// Builds a record, applying redaction and then truncation to the raw arguments and result.
    /// This is the only route to a <see cref="ToolInvocationRecord"/>, so no caller can construct
    /// an unredacted or unbounded one (AC4).
    /// </summary>
    /// <param name="orderIndex">Zero-based execution-order position.</param>
    /// <param name="toolCallId">Provider tool-call correlation id.</param>
    /// <param name="toolName">Invoked tool name.</param>
    /// <param name="rawArguments">Raw serialized arguments, or null.</param>
    /// <param name="rawResultContent">Raw result content, or null.</param>
    /// <param name="isError">Whether the call failed.</param>
    /// <param name="isIncomplete">Whether the call never produced a result.</param>
    /// <param name="startedAt">When the call was issued.</param>
    /// <param name="completedAt">When the result landed; null when incomplete.</param>
    /// <param name="parentToolCallId">Parent call id for nested calls; null otherwise.</param>
    /// <returns>The policy-sanitised record.</returns>
    public ToolInvocationRecord Create(
        int orderIndex,
        string toolCallId,
        string toolName,
        string? rawArguments,
        string? rawResultContent,
        bool isError,
        bool isIncomplete,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        string? parentToolCallId = null)
        => new()
        {
            OrderIndex = orderIndex,
            ToolCallId = toolCallId,
            ToolName = toolName,
            Arguments = Sanitize(rawArguments, MaxArgumentBytes),
            ResultContent = Sanitize(rawResultContent, MaxResultBytes),
            IsError = isError,
            IsIncomplete = isIncomplete,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            ParentToolCallId = parentToolCallId
        };

    /// <summary>
    /// Redacts secret-shaped material, then caps the result on a rune boundary with an explicit
    /// <c>[truncated N bytes]</c> marker. Redaction runs first so a secret can never survive by
    /// straddling the truncation cut.
    /// </summary>
    private string? Sanitize(string? value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return StreamingSessionHelper.TruncateToolResult(_redactor.Redact(value), maxBytes);
    }
}
