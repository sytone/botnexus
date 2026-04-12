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

        await foreach (var evt in stream.WithCancellation(cancellationToken))
        {
            switch (evt.Type)
            {
                case AgentStreamEventType.ContentDelta when evt.ContentDelta is not null:
                    streamedContent.Append(evt.ContentDelta);
                    break;
                case AgentStreamEventType.ToolStart when evt.ToolCallId is not null || evt.ToolName is not null:
                    streamedHistory.Add(new SessionEntry
                    {
                        Role = MessageRole.Tool,
                        Content = $"Tool '{evt.ToolName ?? "unknown"}' started.",
                        ToolName = evt.ToolName,
                        ToolCallId = evt.ToolCallId
                    });
                    break;
                case AgentStreamEventType.ToolEnd when evt.ToolCallId is not null || evt.ToolName is not null:
                    streamedHistory.Add(new SessionEntry
                    {
                        Role = MessageRole.Tool,
                        Content = evt.ToolResult ?? (evt.ToolIsError == true ? "Tool execution failed." : "Tool execution completed."),
                        ToolName = evt.ToolName,
                        ToolCallId = evt.ToolCallId
                    });
                    break;
                case AgentStreamEventType.Error when options.IncludeErrorsInHistory && !string.IsNullOrWhiteSpace(evt.ErrorMessage):
                    streamedHistory.Add(new SessionEntry
                    {
                        Role = MessageRole.System,
                        Content = $"Agent stream error: {evt.ErrorMessage}"
                    });
                    break;
            }

            if (options.OnEventAsync is not null)
            {
                await options.OnEventAsync(evt, cancellationToken);
            }
        }

        session.AddEntries(streamedHistory);
        if (streamedContent.Length > 0)
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = streamedContent.ToString() });
        await sessionStore.SaveAsync(session, cancellationToken);
        if (lifecycleEvents is not null)
        {
            await lifecycleEvents.PublishAsync(
                new SessionLifecycleEvent(
                    session.SessionId,
                    session.AgentId,
                    SessionLifecycleEventType.Closed,
                    session),
                cancellationToken);
        }

        return new StreamingSessionResult(streamedContent.ToString(), streamedHistory);
    }
}

/// <summary>
/// Configures <see cref="StreamingSessionHelper"/> processing behavior.
/// </summary>
/// <param name="IncludeErrorsInHistory">
/// Whether stream error events should be persisted to history as system entries.
/// </param>
/// <param name="OnEventAsync">Optional callback invoked for each stream event.</param>
public sealed record StreamingSessionOptions(
    bool IncludeErrorsInHistory = false,
    Func<AgentStreamEvent, CancellationToken, ValueTask>? OnEventAsync = null);

/// <summary>
/// Represents the accumulated results of stream processing.
/// </summary>
/// <param name="AssistantContent">The full assistant response assembled from deltas.</param>
/// <param name="HistoryEntries">The history entries generated from stream events.</param>
public sealed record StreamingSessionResult(
    string AssistantContent,
    IReadOnlyList<SessionEntry> HistoryEntries);
