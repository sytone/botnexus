using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// The single projection from stored <see cref="GatewaySession"/> history onto the chronological
/// <see cref="ConversationHistoryEntry"/> view rendered by the portal and by every export.
/// </summary>
/// <remarks>
/// <para>
/// Extracted verbatim out of <see cref="ConversationHistoryAssembler"/> for issue #3278. The export
/// document model needs the same boundary-marker / <c>NO_REPLY</c> / crash-sentinel / compaction
/// state machine that the history endpoint uses, and re-deriving it in an exporter is exactly the
/// drift the issue's anti-drift clause forbids. Both callers now share this method, so there is one
/// transcript assembly path in the codebase and a change to the filtering rules cannot apply to the
/// portal but not to a downloaded transcript.
/// </para>
/// <para>
/// Session-scoped export passes a single-element list; conversation-scoped export and the history
/// endpoint pass the conversation's linked sessions in ascending <c>CreatedAt</c> order.
/// </para>
/// </remarks>
public static class ConversationHistoryProjection
{
    /// <summary>
    /// Flattens the ordered session list into a single chronological entry list, inserting a
    /// <c>boundary</c> marker between sessions, dropping <c>NO_REPLY</c> assistant turns (#773),
    /// crash sentinels and contentless assistant ghost rows (#2921), and projecting compaction
    /// summaries as distinct <c>compaction</c> markers. Folded (<c>IsHistory</c>) entries are
    /// retained and flagged <see cref="ConversationHistoryEntry.IsFolded"/> (#2936) so the
    /// pre-compaction transcript stays reachable and can be rendered collapsed.
    /// </summary>
    /// <param name="linkedSessions">Sessions to project, in the order they should appear.</param>
    /// <returns>The chronological entry list.</returns>
    public static List<ConversationHistoryEntry> Project(IReadOnlyList<GatewaySession> linkedSessions)
    {
        ArgumentNullException.ThrowIfNull(linkedSessions);

        var allEntries = new List<ConversationHistoryEntry>();

        for (var i = 0; i < linkedSessions.Count; i++)
        {
            var session = linkedSessions[i];

            // Insert boundary marker before each session except the first.
            if (i > 0)
            {
                var previousSession = linkedSessions[i - 1];
                allEntries.Add(new ConversationHistoryEntry
                {
                    Kind = "boundary",
                    SessionId = previousSession.SessionId.Value,
                    AgentId = previousSession.AgentId.Value,
                    Timestamp = previousSession.UpdatedAt,
                    Reason = "session_end"
                });
            }

            // Append all history entries from this session.
            // Skip assistant entries whose content is exactly "NO_REPLY" (optionally padded with whitespace).
            // These are deliberate cron no-ops that produced no user-facing output; including them in
            // history would show blank turns in the portal for every cron wakeup that had nothing to say (#773).
            var snapshot = session.GetHistorySnapshot();
            foreach (var entry in snapshot)
            {
                // #2936: folded entries (IsHistory) are NOT skipped. IsHistory answers "should this
                // go to the LLM?", not "should this go to the UI?" - the flag's own contract says
                // historical entries stay in the store for transcript fidelity and UI fold/collapse.
                // Filtering them here removed them from the candidate set entirely, making up to 96%
                // of a compacted transcript unreachable from every client. They are now emitted with
                // IsFolded = true so the portal can render them collapsed under the boundary marker.
                //
                // Crash sentinels are genuinely non-content recovery placeholders and stay skipped.
                if (entry.IsCrashSentinel)
                    continue;

                if (entry.Role == MessageRole.Assistant &&
                    string.Equals(entry.Content?.Trim(), "NO_REPLY", StringComparison.Ordinal))
                    continue;

                // #2921: never replay a contentless assistant row as a chat bubble. The fix in
                // StreamingSessionHelper stops NEW ghost rows being written, but the rows already
                // in session_history would keep rendering as empty timestamped bubbles forever.
                // A thinking-only entry (#1198/#656) is deliberately NOT skipped - it has thinking
                // content to show and is a legitimate transcript entry.
                if (entry.Role == MessageRole.Assistant
                    && !entry.IsCompactionSummary
                    && string.IsNullOrWhiteSpace(entry.Content)
                    && string.IsNullOrWhiteSpace(entry.ThinkingContent)
                    && entry.ToolCallId is null
                    && entry.ToolName is null)
                    continue;

                // Emit compaction summaries as distinct boundary markers so the portal
                // can render them as separators rather than normal system messages.
                if (entry.IsCompactionSummary)
                {
                    allEntries.Add(new ConversationHistoryEntry
                    {
                        Kind = "compaction",
                        SessionId = session.SessionId.Value,
                        AgentId = session.AgentId.Value,
                        Timestamp = entry.Timestamp,
                        Reason = "compaction",
                        Content = entry.Content,
                        IsFolded = entry.IsHistory
                    });
                    continue;
                }

                allEntries.Add(new ConversationHistoryEntry
                {
                    Kind = "message",
                    SessionId = session.SessionId.Value,
                    AgentId = session.AgentId.Value,
                    Role = entry.Role.ToString().ToLowerInvariant(),
                    Content = entry.Content,
                    Timestamp = entry.Timestamp,
                    ToolName = entry.ToolName,
                    ToolCallId = entry.ToolCallId,
                    ToolArgs = entry.ToolArgs,
                    ToolIsError = entry.ToolIsError,
                    ThinkingContent = entry.ThinkingContent,
                    // #2840: surface the entry's origin attribution so a script-posted message is
                    // distinguishable from a human turn by a history reader. Null for ordinary turns.
                    SenderId = entry.SenderId,
                    IsFolded = entry.IsHistory,
                    // #2149: project the orthogonal typed kind so live delivery and history replay
                    // agree; ResolveKind maps legacy/unstamped entries to "message".
                    MessageKind = entry.ResolveKind().Value
                });
            }
        }

        return allEntries;
    }
}
