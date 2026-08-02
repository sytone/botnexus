using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Streaming;

namespace BotNexus.Gateway.Audit;

/// <summary>
/// The single execution-layer durable tool-audit sink (issue #2614).
/// </summary>
/// <remarks>
/// <para>
/// Before #2614 there were two independent producers of the same concept: the delivery-layer
/// <see cref="StreamingSessionHelper"/> rendered rich start/end tool rows for streamed runs, while
/// each blocking trigger projected its own single-row shape from <c>AgentResponse.ToolCalls</c>.
/// The audit guarantee therefore depended on which transport the caller happened to pick.
/// </para>
/// <para>
/// This sink is the ONE seam both transports write through. <see cref="StreamAsync"/>-shaped
/// callers use the start/result/incomplete renderers as events arrive; blocking
/// <c>PromptAsync</c> callers hand the settled <see cref="ToolInvocationRecord"/> timeline (the
/// #2613 shared record) to <see cref="ProjectBlockingRun"/>. Both routes emit
/// <see cref="MessageRole.Tool"/> session-history rows of the same shape, so removing the sink
/// call from either path removes the audit record entirely - which is exactly what the #2614
/// mutation tests assert.
/// </para>
/// </remarks>
public interface IToolAuditSink
{
    /// <summary>
    /// Renders the audit row recorded when a tool call is observed to start (streaming boundary).
    /// </summary>
    /// <param name="toolCallId">Provider tool-call correlation id, when known.</param>
    /// <param name="toolName">The invoked tool name, when known.</param>
    /// <param name="serializedArguments">Already-serialized JSON arguments, or null.</param>
    /// <returns>The tool-start history row.</returns>
    SessionEntry ProjectStart(string? toolCallId, string? toolName, string? serializedArguments);

    /// <summary>
    /// Renders the audit row recorded when a tool call produces a result (streaming boundary).
    /// </summary>
    /// <param name="toolCallId">Provider tool-call correlation id, when known.</param>
    /// <param name="toolName">The invoked tool name, when known.</param>
    /// <param name="resultContent">The raw result content, or null when the tool returned none.</param>
    /// <param name="isError">Whether the tool execution failed.</param>
    /// <param name="maxPersistedBytes">Write-time UTF-8 byte cap; non-positive disables it (#1598).</param>
    /// <returns>The tool-result history row.</returns>
    SessionEntry ProjectResult(string? toolCallId, string? toolName, string? resultContent, bool isError, int maxPersistedBytes);

    /// <summary>
    /// Renders the synthesized audit row for a call that started but never produced a result
    /// (#1001 orphan synthesis). Shared by both transports so an interrupted run reads the same
    /// way whichever boundary observed it.
    /// </summary>
    /// <param name="toolCallId">Provider tool-call correlation id.</param>
    /// <param name="toolName">The invoked tool name.</param>
    /// <returns>The synthesized incomplete-call history row, flagged as an error.</returns>
    SessionEntry ProjectIncomplete(string? toolCallId, string toolName);

    /// <summary>
    /// Projects a settled blocking-run tool timeline into ordered audit rows, one per call.
    /// </summary>
    /// <param name="invocations">The run's tool timeline in execution order (#2613 records).</param>
    /// <returns>Ordered <see cref="MessageRole.Tool"/> history rows.</returns>
    IReadOnlyList<SessionEntry> ProjectBlockingRun(IReadOnlyList<ToolInvocationRecord> invocations);

    /// <summary>
    /// Converts a blocking <see cref="AgentResponse"/>'s tool calls into the shared #2613 record
    /// timeline, applying the single <see cref="ToolInvocationRecordPolicy"/> so a blocking run's
    /// records are redacted and bounded exactly like a streamed run's.
    /// </summary>
    /// <param name="response">The blocking-run response.</param>
    /// <returns>The tool timeline in execution order; empty when no tools ran.</returns>
    IReadOnlyList<ToolInvocationRecord> CaptureBlockingRun(AgentResponse response);
}

/// <summary>
/// The default, stateless <see cref="IToolAuditSink"/> registered once in gateway composition
/// (issue #2614 AC1).
/// </summary>
/// <remarks>
/// Rendering is byte-identical to the pre-#2614 behaviour of both producers, so migrating the
/// streaming helper and the blocking triggers onto this sink is a pure consolidation: same row
/// count, same ordering, same rendered text. The only additive change is on the blocking path,
/// where arguments and results now travel through <see cref="ToolInvocationRecordPolicy"/>.
/// </remarks>
public sealed class DefaultToolAuditSink : IToolAuditSink
{
    private readonly ToolInvocationRecordPolicy _policy;

    /// <summary>
    /// Creates a sink over a record policy.
    /// </summary>
    /// <param name="policy">Optional policy override; defaults to <see cref="ToolInvocationRecordPolicy.Default"/>.</param>
    public DefaultToolAuditSink(ToolInvocationRecordPolicy? policy = null)
        => _policy = policy ?? ToolInvocationRecordPolicy.Default;

    /// <summary>
    /// The process-wide default instance, used by call sites that predate constructor injection
    /// (the static <see cref="StreamingSessionHelper"/> in particular). Composition still registers
    /// the sink in DI so there is exactly one configured implementation.
    /// </summary>
    public static IToolAuditSink Instance { get; } = new DefaultToolAuditSink();

    /// <inheritdoc/>
    public SessionEntry ProjectStart(string? toolCallId, string? toolName, string? serializedArguments)
        => new()
        {
            Role = MessageRole.Tool,
            Content = $"Tool '{toolName ?? "unknown"}' started.",
            ToolName = toolName,
            ToolCallId = toolCallId,
            ToolArgs = serializedArguments
        };

    /// <inheritdoc/>
    public SessionEntry ProjectResult(string? toolCallId, string? toolName, string? resultContent, bool isError, int maxPersistedBytes)
    {
        var content = resultContent ?? (isError ? "Tool execution failed." : "Tool execution completed.");
        return new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = StreamingSessionHelper.TruncateToolResult(content, maxPersistedBytes),
            ToolName = toolName,
            ToolCallId = toolCallId,
            ToolIsError = isError
        };
    }

    /// <inheritdoc/>
    public SessionEntry ProjectIncomplete(string? toolCallId, string toolName)
        => new()
        {
            Role = MessageRole.Tool,
            Content = $"Tool '{toolName}' did not complete - result synthesized for transcript consistency.",
            ToolName = toolName,
            ToolCallId = toolCallId,
            ToolIsError = true
        };

    /// <inheritdoc/>
    public IReadOnlyList<SessionEntry> ProjectBlockingRun(IReadOnlyList<ToolInvocationRecord> invocations)
    {
        var rows = new List<SessionEntry>(invocations.Count);
        foreach (var record in invocations)
        {
            if (record.IsIncomplete)
            {
                var incomplete = ProjectIncomplete(record.ToolCallId, record.ToolName);
                rows.Add(incomplete with { ToolArgs = record.Arguments });
                continue;
            }

            var row = ProjectResult(record.ToolCallId, record.ToolName, record.ResultContent, record.IsError, maxPersistedBytes: 0);
            rows.Add(row with { ToolArgs = record.Arguments });
        }

        return rows;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ToolInvocationRecord> CaptureBlockingRun(AgentResponse response)
    {
        var records = new List<ToolInvocationRecord>(response.ToolCalls.Count);
        for (var index = 0; index < response.ToolCalls.Count; index++)
        {
            var call = response.ToolCalls[index];
            records.Add(_policy.Create(
                orderIndex: index,
                toolCallId: call.ToolCallId,
                toolName: call.ToolName,
                rawArguments: call.Arguments,
                rawResultContent: call.ResultContent,
                isError: call.IsError,
                isIncomplete: call.IsIncomplete,
                startedAt: null,
                completedAt: null));
        }

        return records;
    }
}
