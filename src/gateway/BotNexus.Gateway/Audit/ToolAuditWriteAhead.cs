using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Audit;

/// <summary>
/// The fail-closed tool-audit write-ahead (issue #2615, the security slice of #2127).
/// </summary>
/// <remarks>
/// <para>
/// The earlier slices made tool activity <i>available</i>: #2113 wrote sub-agent tool starts ahead
/// of execution, #2613 gave both transports one record shape, and #2614 gave them one rendering
/// sink. None of them made the guarantee <i>binding</i> - a top-level agent's tool call was never
/// written ahead at all, so a crash mid-tool left no evidence that the tool had ever been invoked,
/// and the only surviving account of a destructive command was the agent's own prose.
/// </para>
/// <para>
/// This type closes that gap for every agent, not just sub-agents, and enforces two properties:
/// </para>
/// <list type="number">
/// <item>
/// <b>Write ahead, then execute.</b> <see cref="PersistStartAsync"/> does not return until the
/// start record is durable. When it cannot be persisted, a side-effecting tool is <b>blocked</b>
/// (the call throws before the tool is ever reached). Read-only tools keep the historical
/// best-effort behaviour, because blocking them would convert an audit-durability incident into a
/// total agent outage without adding any containment.
/// </item>
/// <item>
/// <b>An interruption is explicit, never silent.</b> A call that started and never reported a
/// result is closed out by <see cref="RecordInterruptedAsync"/> with the shared
/// <see cref="IToolAuditSink.ProjectIncomplete"/> row, so cancellation, timeout and process-level
/// termination all read the same way instead of the invocation simply vanishing.
/// </item>
/// </list>
/// <para>
/// Rows are rendered by the single #2614 sink rather than hand-assembled here, so the write-ahead
/// row and the row the streaming/blocking boundaries persist cannot drift apart in shape.
/// </para>
/// </remarks>
internal sealed class ToolAuditWriteAhead(
    ISessionStore? sessionStore,
    IToolAuditSink auditSink,
    ISecretRedactor redactor,
    SessionId sessionId,
    ILogger logger)
{
    /// <summary>
    /// Tools whose execution can change the world outside the transcript. A durability failure on
    /// one of these fails CLOSED: the call is refused rather than executed unrecorded.
    /// </summary>
    private static readonly HashSet<string> FailClosedTools =
        new(["exec", "shell", "process"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Calls observed to have started and not yet reported a result, keyed by tool-call id. An
    /// entry survives here exactly as long as the invocation is unaccounted for, which is what
    /// makes the interrupted set computable without re-reading the transcript.
    /// </summary>
    private readonly ConcurrentDictionary<string, InFlightCall> _inFlight = new(StringComparer.Ordinal);

    private int _interruptionRecorded;

    /// <summary>
    /// Persists the redacted invocation before the tool is invoked, and does not return until the
    /// record is durable.
    /// </summary>
    /// <param name="toolCallId">Provider tool-call correlation id.</param>
    /// <param name="toolName">The tool about to be invoked.</param>
    /// <param name="arguments">The validated arguments the tool will receive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the record could not be persisted and <paramref name="toolName"/> is
    /// side-effecting. The tool is not invoked.
    /// </exception>
    public async Task PersistStartAsync(
        string toolCallId,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var serializedArguments = redactor.Redact(JsonSerializer.Serialize(arguments));

        // Track the call BEFORE attempting the write. A process that dies during the write is
        // exactly the case an interrupted record must survive, and a call that is blocked below is
        // removed again so it is never reported as interrupted.
        _inFlight[toolCallId] = new InFlightCall(toolName, serializedArguments);

        try
        {
            var store = sessionStore
                ?? throw new InvalidOperationException("Session persistence is unavailable.");
            var session = await store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Session '{sessionId}' does not exist.");

            session.AddEntry(auditSink.ProjectStart(toolCallId, toolName, serializedArguments));
            await store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            GatewayTelemetry.ToolAuditWriteAheadFailures.Add(1,
                new KeyValuePair<string, object?>("botnexus.tool.name", toolName),
                new KeyValuePair<string, object?>("botnexus.session.id", sessionId.Value));
            logger.LogError(ex,
                "Failed to persist tool start for tool '{ToolName}', call '{ToolCallId}', session '{SessionId}'.",
                toolName, toolCallId, sessionId);

            if (FailClosedTools.Contains(toolName))
            {
                // The tool never runs, so it is not in flight and must not later be reported as an
                // interrupted invocation - it was refused, which is a different fact.
                _inFlight.TryRemove(toolCallId, out _);
                throw new InvalidOperationException(
                    $"Tool '{toolName}' was blocked because its invocation could not be durably recorded.", ex);
            }
        }
    }

    /// <summary>
    /// Marks a call as accounted for, so it is not later reported as an interrupted invocation.
    /// </summary>
    /// <param name="toolCallId">The tool-call id that produced a result.</param>
    public void RecordCompleted(string toolCallId) => _inFlight.TryRemove(toolCallId, out _);

    /// <summary>
    /// Closes out every call that started and never reported a result, writing the shared
    /// incomplete row for each. Called when a run is cancelled, times out, or otherwise unwinds
    /// after a tool started.
    /// </summary>
    /// <param name="cancellationToken">
    /// The token that ended the run. It is deliberately NOT used to govern this write: the run is
    /// already cancelled, and honouring an already-cancelled token here would abandon the very
    /// record the interruption exists to produce. It is accepted so callers can pass the
    /// cancellation or timeout token that ended the turn without having to reason about which of
    /// the two shapes they hold (AC4).
    /// </param>
    public async Task RecordInterruptedAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (_inFlight.IsEmpty)
            return;

        // Idempotence: a run can unwind through more than one handler (the isolation strategy's
        // catch and the caller's finally), and a duplicated incomplete row would misreport one
        // interrupted call as two.
        if (Interlocked.Exchange(ref _interruptionRecorded, 1) == 1)
            return;

        try
        {
            if (sessionStore is null)
                return;

            var session = await sessionStore.GetAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (session is null)
                return;

            foreach (var (toolCallId, call) in _inFlight.ToArray())
            {
                if (!_inFlight.TryRemove(toolCallId, out _))
                    continue;

                session.AddEntry(auditSink.ProjectIncomplete(toolCallId, call.ToolName, call.SerializedArguments));
            }

            await sessionStore.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The turn is already failing. Surfacing an audit-write error here would replace the
            // real cause with a bookkeeping one; fail-closed governs execution, not the post-mortem.
            GatewayTelemetry.ToolAuditWriteAheadFailures.Add(1,
                new KeyValuePair<string, object?>("botnexus.session.id", sessionId.Value));
            logger.LogError(ex,
                "Failed to record interrupted tool invocations for session '{SessionId}'.", sessionId);
        }
    }

    private sealed record InFlightCall(string ToolName, string SerializedArguments);
}
