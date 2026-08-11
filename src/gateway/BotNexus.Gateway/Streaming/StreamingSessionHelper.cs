using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Streaming;

/// <summary>
/// Processes agent streaming events, updates session history, and persists the session.
/// </summary>
public static class StreamingSessionHelper
{
    /// <summary>
    /// Processes an agent stream, records session history entries, and saves the session.
    /// </summary>
    /// <param name="stream">The stream of agent events to process.</param>
    /// <param name="session">The target session to update.</param>
    /// <param name="sessionStore">The session store used to persist changes.</param>
    /// <param name="options">Optional behavior overrides.</param>
    /// <param name="lifecycleEvents">Optional lifecycle event publisher.</param>
    /// <param name="finalSaveFence">
    /// Optional run-identity fence (issue #1518). When supplied, the <b>final authoritative
    /// post-run write</b> is fenced against it: if the session was deleted, sealed by a reset,
    /// or rebound to another conversation while the stream was in flight, the final save (and
    /// the <c>Closed</c> lifecycle publish) is skipped instead of resurrecting or clobbering the
    /// row. The eager mid-run write-ahead flushes (tool-start / turn-end) remain best-effort and
    /// unfenced - they only ever run while the session is still the run's live session.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The accumulated streaming result details.</returns>
    public static async Task<StreamingSessionResult> ProcessAndSaveAsync(
        IAsyncEnumerable<AgentStreamEvent> stream,
        GatewaySession session,
        ISessionStore sessionStore,
        StreamingSessionOptions? options = null,
        SessionLifecycleEvents? lifecycleEvents = null,
        SessionWriteFence? finalSaveFence = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StreamingSessionOptions();
        // #2614: every tool history row this delivery-layer helper persists is rendered by the ONE
        // execution-layer audit sink the blocking PromptAsync callers also write through. The helper
        // no longer owns a tool-entry format of its own.
        var auditSink = options.ToolAuditSink ?? DefaultToolAuditSink.Instance;
        // #2149: the orthogonal typed kind to stamp on assistant entries this run produces. The
        // default (MessageKind.Message) stores as null so ordinary streamed responses are
        // unchanged; a subagent-response run carries the distinct kind through every flush path so
        // live delivery and history replay agree.
        var assistantKind = options.AssistantMessageKind is { } k && !k.Equals(MessageKind.Message)
            ? k
            : null;
        var streamedContent = new StringBuilder();
        var streamedHistory = new List<SessionEntry>();
        var allHistoryEntries = new List<SessionEntry>();
        var hadThinkingContent = false;
        var thinkingBuffer = new StringBuilder();
        var hadMessageEnd = false;
        var toolStartIds = new HashSet<string>(StringComparer.Ordinal);
        var toolEndIds = new HashSet<string>(StringComparer.Ordinal);
        var toolStartEntries = new Dictionary<string, SessionEntry>(StringComparer.Ordinal);
        // #2613: the streaming half of the shared tool-invocation timeline. Records are appended in
        // ToolStart order (so OrderIndex is execution order) and completed in place on ToolEnd, then
        // surfaced on StreamingSessionResult so this boundary and the blocking PromptAsync boundary
        // hand back the SAME record shape produced by the SAME policy.
        var toolInvocationBuilders = new List<ToolInvocationBuilder>();
        var toolInvocationIndex = new Dictionary<string, ToolInvocationBuilder>(StringComparer.Ordinal);

        // Apply stall watchdog if configured — wraps the stream with inactivity timeout.
        var effectiveStream = options.StallWatchdog is not null
            ? options.StallWatchdog.WrapAsync(stream, cancellationToken)
            : stream;

        await foreach (var evt in effectiveStream.WithCancellation(cancellationToken))
        {
            switch (evt.Type)
            {
                case AgentStreamEventType.ContentDelta when evt.ContentDelta is not null:
                    streamedContent.Append(evt.ContentDelta);
                    break;
                case AgentStreamEventType.ThinkingDelta when evt.ThinkingContent is not null:
                    hadThinkingContent = true;
                    thinkingBuffer.Append(evt.ThinkingContent);
                    break;
                case AgentStreamEventType.MessageEnd:
                    hadMessageEnd = true;
                    // #2522 producer seam: a MessageEnd carries the provider's reported usage for
                    // the request that produced this assistant message, and the session is writable
                    // and about to be persisted by the flush below. Stamp the prompt-token count
                    // here so the compactor's measure-first diagnostic (which already reads this
                    // key) stops rendering "unavailable". Diagnostic only - no compaction
                    // behaviour is gated on it.
                    ProviderTokenUsageRecorder.Record(session, evt.Usage);
                    break;
                case AgentStreamEventType.ToolStart when evt.ToolCallId is not null || evt.ToolName is not null:
                    var startEntry = auditSink.ProjectStart(
                        evt.ToolCallId,
                        evt.ToolName,
                        evt.ToolArgs is { Count: > 0 }
                            ? System.Text.Json.JsonSerializer.Serialize(evt.ToolArgs)
                            : null);
                    var alreadyPersisted = evt.ToolCallId is not null
                        && session.GetHistorySnapshot().Any(entry =>
                            entry.ToolCallId == evt.ToolCallId && entry.IsToolStartRow());
                    if (!alreadyPersisted)
                    {
                        streamedHistory.Add(startEntry);
                    }
                    allHistoryEntries.Add(startEntry);
                    if (evt.ToolCallId is not null)
                    {
                        toolStartIds.Add(evt.ToolCallId);
                        toolStartEntries[evt.ToolCallId] = startEntry;
                    }

                    // #2613: open a record for this call at its start, capturing the arguments and
                    // the observed start time. A duplicate ToolStart for the same id does not open
                    // a second record - ordering is defined by first observation.
                    var startKey = evt.ToolCallId ?? evt.ToolName ?? "unknown";
                    if (!toolInvocationIndex.ContainsKey(startKey))
                    {
                        var builder = new ToolInvocationBuilder
                        {
                            ToolCallId = startKey,
                            ToolName = evt.ToolName ?? "unknown",
                            Arguments = startEntry.ToolArgs,
                            StartedAt = evt.Timestamp
                        };
                        toolInvocationBuilders.Add(builder);
                        toolInvocationIndex[startKey] = builder;
                    }

                    // Write-ahead: persist tool start immediately so the entry
                    // survives browser refresh or session stall (#1052).
                    session.AddEntries(streamedHistory.ToList());
                    streamedHistory.Clear();
                    // Do NOT remove the crash sentinel on this mid-run tool-start write-ahead
                    // (#2135). The sentinel is a durable lease for the entire agent run; a tool
                    // turn is a continuation, not a terminal boundary. It is removed only at the
                    // final authoritative completion save below.
                    try
                    {
                        await sessionStore.SaveAsync(session, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Best-effort write-ahead — log but don't abort.
                        _ = ex;
                    }
                    break;
                case AgentStreamEventType.ToolEnd when evt.ToolCallId is not null || evt.ToolName is not null:
                    // #2614: the sink renders the result row and applies the write-time #1598 cap,
                    // so the streamed and blocking boundaries cannot drift in format.
                    // #2906: re-supply the arguments the paired ToolStart carried so the RESULT row
                    // is self-describing. They are already in hand here - the start entry for this
                    // tool_call_id is held in toolStartEntries - so the value never has to be
                    // recovered later by a self-join over the transcript.
                    var resultArgs = evt.ToolCallId is not null
                        && toolStartEntries.TryGetValue(evt.ToolCallId, out var pairedStart)
                        ? pairedStart.ToolArgs
                        : null;
                    streamedHistory.Add(auditSink.ProjectResult(
                        evt.ToolCallId,
                        evt.ToolName,
                        evt.ToolResult,
                        evt.ToolIsError == true,
                        options.MaxPersistedToolResultBytes,
                        resultArgs));
                    allHistoryEntries.Add(streamedHistory[^1]);
                    if (evt.ToolCallId is not null)
                        toolEndIds.Add(evt.ToolCallId);

                    // #2613: close the matching record with the raw result and error state. The
                    // record's own byte budget and secret sweep are applied by the policy at
                    // construction time, independently of the history write-time cap above.
                    var endKey = evt.ToolCallId ?? evt.ToolName ?? "unknown";
                    if (toolInvocationIndex.TryGetValue(endKey, out var endBuilder))
                    {
                        endBuilder.ResultContent = evt.ToolResult;
                        endBuilder.IsError = evt.ToolIsError == true;
                        endBuilder.CompletedAt = evt.Timestamp;
                        endBuilder.Completed = true;
                    }
                    else
                    {
                        // Defensive: a ToolEnd with no observed ToolStart still belongs on the
                        // timeline rather than being silently dropped.
                        var orphan = new ToolInvocationBuilder
                        {
                            ToolCallId = endKey,
                            ToolName = evt.ToolName ?? "unknown",
                            ResultContent = evt.ToolResult,
                            IsError = evt.ToolIsError == true,
                            StartedAt = evt.Timestamp,
                            CompletedAt = evt.Timestamp,
                            Completed = true
                        };
                        toolInvocationBuilders.Add(orphan);
                        toolInvocationIndex[endKey] = orphan;
                    }

                    break;
                case AgentStreamEventType.Error when options.IncludeErrorsInHistory && !string.IsNullOrWhiteSpace(evt.ErrorMessage):
                    streamedHistory.Add(new SessionEntry
                    {
                        Role = MessageRole.System,
                        Content = $"Agent stream error: {evt.ErrorMessage}"
                    });
                    allHistoryEntries.Add(streamedHistory[^1]);
                    break;
                case AgentStreamEventType.TurnEnd when streamedHistory.Count > 0 || streamedContent.Length > 0:
                    // Flush accumulated entries at each turn boundary so a second client
                    // opening the session mid-run sees partial progress rather than nothing.
                    // The final SaveAsync below remains the authoritative write for the
                    // completed assistant message; this flush persists tool calls eagerly.
                    // Fixes #362.
                    var turnSnapshot = streamedHistory.ToList();
                    streamedHistory.Clear();
                    if (streamedContent.Length > 0)
                    {
                        turnSnapshot.Add(new SessionEntry { Role = MessageRole.Assistant, Content = streamedContent.ToString(), ThinkingContent = thinkingBuffer.Length > 0 ? thinkingBuffer.ToString() : null, Kind = assistantKind });
                        thinkingBuffer.Clear();
                        streamedContent.Clear();
                    }
                    session.AddEntries(turnSnapshot);
                    // Do NOT remove the crash sentinel here (#2135). A streamed agent run can
                    // emit another MessageStart/tool turn after this intermediate TurnEnd; the
                    // sentinel is a durable lease for the ENTIRE run, not one streamed model turn.
                    // Clearing it at each intermediate TurnEnd left in-flight work with no
                    // replayable marker if the process died before the run reached its final,
                    // authoritative completion save (which is the only place the sentinel is
                    // removed - see below).
                    try
                    {
                        await sessionStore.SaveAsync(session, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Best-effort mid-run flush — log but don't abort the stream.
                        // The final SaveAsync at AgentEnd is still authoritative.
                        _ = ex; // Suppress unused-variable warning; caller's logger not available here.
                    }
                    break;
            }

            if (options.OnEventAsync is not null)
            {
                await options.OnEventAsync(evt, cancellationToken);
            }
        }

        // Synthesize failed tool results for orphan tool calls (#1001).
        // A ToolStart with no matching ToolEnd means the tool call did not complete —
        // append a failed result entry so the transcript is consistent and downstream
        // consumers (e.g. provider message builders) do not encounter an orphan call.
        foreach (var orphanId in toolStartIds.Except(toolEndIds))
        {
            var hasStart = toolStartEntries.TryGetValue(orphanId, out var entry);
            var toolName = hasStart ? entry!.ToolName ?? "unknown" : "unknown";
            // #2906: an interrupted call is exactly the case forensics needs the arguments for, so
            // the synthesized row carries them too.
            var orphanEntry = auditSink.ProjectIncomplete(orphanId, toolName, hasStart ? entry!.ToolArgs : null);
            streamedHistory.Add(orphanEntry);
            allHistoryEntries.Add(orphanEntry);
        }

        // Final write: append any remaining content not yet flushed by a TurnEnd
        // (single-turn runs, or the last partial turn before AgentEnd).
        session.AddEntries(streamedHistory);
        var finalContent = streamedContent.ToString();
        if (!string.IsNullOrWhiteSpace(finalContent))
        {
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = finalContent, ThinkingContent = thinkingBuffer.Length > 0 ? thinkingBuffer.ToString() : null, Kind = assistantKind });
        }
        else if (streamedHistory.Count == 0 && hadThinkingContent && hadMessageEnd)
        {
            // The model produced only reasoning/thinking blocks and no visible text or tool calls.
            // Add an empty assistant entry so the transcript is in a valid state (prevents the
            // duplicate-message replay bug from #656). Do NOT surface any user-visible message —
            // thinking-only responses are a normal model behaviour, not an error (#1198).
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.Assistant,
                Content = string.Empty,
                Kind = assistantKind
            });
        }

        // Remove crash sentinel on clean completion (#363).
        session.RemoveCrashSentinels();

        // #1518: the final write is the authoritative post-run finalizer save. When a fence was
        // supplied, honour it so a delete/reset that landed mid-stream cannot be undone here. On
        // a rebound we skip the save AND the Closed lifecycle publish - the session this run owned
        // no longer exists (or belongs to someone else), so emitting Closed for it would be wrong.
        var finalOutcome = finalSaveFence is { } fence
            ? await sessionStore.SaveAsync(session, fence, cancellationToken)
            : await SaveUnfencedAsync(sessionStore, session, cancellationToken);

        if (finalOutcome == SessionSaveOutcome.Persisted && lifecycleEvents is not null)
        {
            await lifecycleEvents.PublishAsync(
                new SessionLifecycleEvent(
                    session.SessionId.Value,
                    session.AgentId.Value,
                    SessionLifecycleEventType.Closed,
                    session),
                cancellationToken);
        }

        // #2613: materialise the tool timeline through the single shared policy, so the streaming
        // boundary hands back exactly the record shape the blocking boundary produces.
        var toolInvocations = toolInvocationBuilders
            .Select((builder, index) => ToolInvocationRecordPolicy.Default.Create(
                orderIndex: index,
                toolCallId: builder.ToolCallId,
                toolName: builder.ToolName,
                rawArguments: builder.Arguments,
                rawResultContent: builder.ResultContent,
                isError: builder.Completed ? builder.IsError : true,
                isIncomplete: !builder.Completed,
                startedAt: builder.StartedAt,
                completedAt: builder.Completed ? builder.CompletedAt : null))
            .ToList();

        return new StreamingSessionResult(streamedContent.ToString(), allHistoryEntries, toolInvocations);
    }

    /// <summary>
    /// Performs an unfenced final save and reports it as <see cref="SessionSaveOutcome.Persisted"/>,
    /// so the caller can treat the fenced and unfenced paths uniformly. Used when no
    /// <see cref="SessionWriteFence"/> was supplied (issue #1518 back-compat for existing callers).
    /// </summary>
    private static async Task<SessionSaveOutcome> SaveUnfencedAsync(
        ISessionStore sessionStore,
        GatewaySession session,
        CancellationToken cancellationToken)
    {
        await sessionStore.SaveAsync(session, cancellationToken);
        return SessionSaveOutcome.Persisted;
    }

    /// <summary>
    /// Caps an oversized tool result at write time (#1598). When the UTF-8 byte size of
    /// <paramref name="content"/> exceeds <paramref name="maxBytes"/>, the content is cut on a
    /// rune boundary (never splitting a surrogate pair or a multi-byte UTF-8 sequence) and an
    /// explicit <c>[truncated N bytes]</c> marker reporting the number of omitted bytes is
    /// appended. A non-positive <paramref name="maxBytes"/> disables truncation.
    /// </summary>
    /// <param name="content">The raw tool result content.</param>
    /// <param name="maxBytes">Maximum UTF-8 byte budget for the retained content; <c>0</c> or negative disables the cap.</param>
    /// <returns>The original content when within budget; otherwise a rune-safe truncated form with a marker.</returns>
    public static string TruncateToolResult(string content, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(content))
            return content;

        var totalBytes = Encoding.UTF8.GetByteCount(content);
        if (totalBytes <= maxBytes)
            return content;

        // Walk runes, accumulating UTF-8 byte cost, and stop before the budget is exceeded.
        // Cutting on a Rune boundary guarantees no lone surrogate and no split multi-byte sequence.
        var span = content.AsSpan();
        var retainedBytes = 0;
        var retainedChars = 0;
        while (retainedChars < span.Length
            && Rune.DecodeFromUtf16(span[retainedChars..], out var rune, out var charsConsumed) == System.Buffers.OperationStatus.Done)
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (retainedBytes + runeBytes > maxBytes)
                break;
            retainedBytes += runeBytes;
            retainedChars += charsConsumed;
        }

        var omittedBytes = totalBytes - retainedBytes;
        // Edge case: if even the first rune does not fit the budget, retainedChars is 0 and the
        // result is just the marker — still strictly smaller than the original oversized blob.
        return string.Concat(span[..retainedChars], $"\n…[truncated {omittedBytes} bytes]");
    }
}

/// <summary>
/// Configures <see cref="StreamingSessionHelper"/> processing behavior.
/// </summary>
/// <param name="IncludeErrorsInHistory">
/// Whether stream error events should be persisted to history as system entries.
/// </param>
/// <param name="OnEventAsync">Optional callback invoked for each stream event.</param>
/// <param name="StallWatchdog">
/// Optional provider stall watchdog. When set, the incoming stream is wrapped with
/// inactivity timeout detection that synthesizes an error event if the provider
/// stops responding.
/// </param>
/// <param name="MaxPersistedToolResultBytes">
/// Maximum UTF-8 byte size of a single tool result persisted to session history. When a
/// result exceeds this cap it is truncated at write time (on a rune boundary) and an explicit
/// <c>[truncated N bytes]</c> marker is appended, so the oversized blob never lands in
/// <c>session_history</c> nor gets re-sent to the model on the next turn. <c>0</c> (the default)
/// disables the cap. See issue #1598.
/// </param>
public sealed record StreamingSessionOptions(
    bool IncludeErrorsInHistory = false,
    Func<AgentStreamEvent, CancellationToken, ValueTask>? OnEventAsync = null,
    ProviderStallWatchdog? StallWatchdog = null,
    int MaxPersistedToolResultBytes = 0,
    MessageKind? AssistantMessageKind = null,
    IToolAuditSink? ToolAuditSink = null);

/// <summary>
/// Represents the accumulated results of stream processing.
/// </summary>
/// <param name="AssistantContent">The full assistant response assembled from deltas.</param>
/// <param name="HistoryEntries">The history entries generated from stream events.</param>
/// <param name="ToolInvocations">
/// The run's tool timeline in execution order (issue #2613). Same record type, produced by the
/// same <see cref="ToolInvocationRecordPolicy"/>, as the blocking <c>PromptAsync</c> boundary
/// projects - so a streamed run and a blocking run of the same tool sequence are observably
/// equivalent rather than two parallel shapes.
/// </param>
public sealed record StreamingSessionResult(
    string AssistantContent,
    IReadOnlyList<SessionEntry> HistoryEntries,
    IReadOnlyList<ToolInvocationRecord>? ToolInvocations = null)
{
    /// <summary>
    /// The run's tool timeline in execution order; never null, empty when the run used no tools.
    /// </summary>
    public IReadOnlyList<ToolInvocationRecord> ToolInvocations { get; init; } = ToolInvocations ?? [];
}

/// <summary>
/// Mutable in-flight accumulator for one streamed tool call. A stream reveals a call's arguments
/// at <c>ToolStart</c> and its result at <c>ToolEnd</c>, so the record cannot be built in one shot;
/// this holds the halves until the run settles, at which point every entry is materialised through
/// <see cref="ToolInvocationRecordPolicy"/>. Internal by design - it is scaffolding, not a second
/// public tool-timeline shape (#2613 AC3).
/// </summary>
internal sealed class ToolInvocationBuilder
{
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public string? Arguments { get; init; }
    public string? ResultContent { get; set; }
    public bool IsError { get; set; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Completed { get; set; }
}
