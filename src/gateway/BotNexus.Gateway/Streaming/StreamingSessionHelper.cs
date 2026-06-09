using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The accumulated streaming result details.</returns>
    public static async Task<StreamingSessionResult> ProcessAndSaveAsync(
        IAsyncEnumerable<AgentStreamEvent> stream,
        GatewaySession session,
        ISessionStore sessionStore,
        StreamingSessionOptions? options = null,
        SessionLifecycleEvents? lifecycleEvents = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StreamingSessionOptions();
        var streamedContent = new StringBuilder();
        var streamedHistory = new List<SessionEntry>();
        var allHistoryEntries = new List<SessionEntry>();
        var hadThinkingContent = false;
        var hadMessageEnd = false;
        var toolStartIds = new HashSet<string>(StringComparer.Ordinal);
        var toolEndIds = new HashSet<string>(StringComparer.Ordinal);
        var toolStartEntries = new Dictionary<string, SessionEntry>(StringComparer.Ordinal);

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
                    break;
                case AgentStreamEventType.MessageEnd:
                    hadMessageEnd = true;
                    break;
                case AgentStreamEventType.ToolStart when evt.ToolCallId is not null || evt.ToolName is not null:
                    var startEntry = new SessionEntry
                    {
                        Role = MessageRole.Tool,
                        Content = $"Tool '{evt.ToolName ?? "unknown"}' started.",
                        ToolName = evt.ToolName,
                        ToolCallId = evt.ToolCallId,
                        ToolArgs = evt.ToolArgs is { Count: > 0 }
                            ? System.Text.Json.JsonSerializer.Serialize(evt.ToolArgs)
                            : null
                    };
                    streamedHistory.Add(startEntry);
                    allHistoryEntries.Add(startEntry);
                    if (evt.ToolCallId is not null)
                    {
                        toolStartIds.Add(evt.ToolCallId);
                        toolStartEntries[evt.ToolCallId] = startEntry;
                    }

                    // Write-ahead: persist tool start immediately so the entry
                    // survives browser refresh or session stall (#1052).
                    session.AddEntries(streamedHistory.ToList());
                    streamedHistory.Clear();
                    session.RemoveCrashSentinels();
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
                    streamedHistory.Add(new SessionEntry
                    {
                        Role = MessageRole.Tool,
                        Content = evt.ToolResult ?? (evt.ToolIsError == true ? "Tool execution failed." : "Tool execution completed."),
                        ToolName = evt.ToolName,
                        ToolCallId = evt.ToolCallId,
                        ToolIsError = evt.ToolIsError == true
                    });
                    allHistoryEntries.Add(streamedHistory[^1]);
                    if (evt.ToolCallId is not null)
                        toolEndIds.Add(evt.ToolCallId);
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
                        turnSnapshot.Add(new SessionEntry { Role = MessageRole.Assistant, Content = streamedContent.ToString() });
                        streamedContent.Clear();
                    }
                    session.AddEntries(turnSnapshot);
                    // Remove crash sentinel before flushing partial turn state.
                    session.RemoveCrashSentinels();
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
            var toolName = toolStartEntries.TryGetValue(orphanId, out var entry)
                ? entry.ToolName ?? "unknown"
                : "unknown";
            var orphanEntry = new SessionEntry
            {
                Role = MessageRole.Tool,
                Content = $"Tool '{toolName}' did not complete — result synthesized for transcript consistency.",
                ToolName = toolName,
                ToolCallId = orphanId,
                ToolIsError = true
            };
            streamedHistory.Add(orphanEntry);
            allHistoryEntries.Add(orphanEntry);
        }

        // Final write: append any remaining content not yet flushed by a TurnEnd
        // (single-turn runs, or the last partial turn before AgentEnd).
        session.AddEntries(streamedHistory);
        var finalContent = streamedContent.ToString();
        if (!string.IsNullOrWhiteSpace(finalContent))
        {
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = finalContent });
        }
        else if (streamedHistory.Count == 0 && hadThinkingContent && hadMessageEnd)
        {
            // The model produced only reasoning/thinking blocks and no visible text or tool calls.
            // Without this sentinel the session transcript ends with the user message and no
            // assistant reply, so the next turn replays the abandoned user prompt, causing
            // duplicate-message confusion and skipped tool calls (same pattern as #656).
            // Surface a system entry so the conversation is in a known good state.
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.System,
                Content = "Agent produced only reasoning content and could not generate a visible response. Please try again or rephrase your message."
            });
        }

        // Remove crash sentinel on clean completion (#363).
        session.RemoveCrashSentinels();
        await sessionStore.SaveAsync(session, cancellationToken);
        if (lifecycleEvents is not null)
        {
            await lifecycleEvents.PublishAsync(
                new SessionLifecycleEvent(
                    session.SessionId.Value,
                    session.AgentId.Value,
                    SessionLifecycleEventType.Closed,
                    session),
                cancellationToken);
        }

        return new StreamingSessionResult(streamedContent.ToString(), allHistoryEntries);
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
public sealed record StreamingSessionOptions(
    bool IncludeErrorsInHistory = false,
    Func<AgentStreamEvent, CancellationToken, ValueTask>? OnEventAsync = null,
    ProviderStallWatchdog? StallWatchdog = null);

/// <summary>
/// Represents the accumulated results of stream processing.
/// </summary>
/// <param name="AssistantContent">The full assistant response assembled from deltas.</param>
/// <param name="HistoryEntries">The history entries generated from stream events.</param>
public sealed record StreamingSessionResult(
    string AssistantContent,
    IReadOnlyList<SessionEntry> HistoryEntries);
