using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Default <see cref="IConversationHistoryAssembler"/> that reads sessions from the session store
/// and projects them into the chronological history view rendered by the portal.
/// </summary>
/// <remarks>
/// This logic was lifted verbatim out of <see cref="ConversationsController.GetHistory"/>. Keeping
/// it in a dedicated, directly-constructable service means the boundary-marker / <c>NO_REPLY</c> /
/// fold / compaction state machine can be exercised by unit tests without an MVC pipeline, and the
/// same assembled view can be reused by the SignalR/portal path.
/// </remarks>
public sealed class ConversationHistoryAssembler : IConversationHistoryAssembler
{
    /// <summary>
    /// Hard ceiling on the number of entries a single page may return once a folded run has been
    /// expanded (#2936). Folded rows render collapsed, so returning a whole run at once is cheap for
    /// the client and removes the ~137-round-trip walk a 20-row page implies over a 2,700-row
    /// compacted transcript - but an unbounded expansion would let one request materialise an
    /// arbitrarily large transcript, so the run is chunked at this size instead.
    /// </summary>
    public const int MaxFoldedPageEntries = 500;

    private readonly IConversationStore _conversations;
    private readonly ISessionStore _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationHistoryAssembler"/> class.
    /// </summary>
    /// <param name="conversations">The conversation store (used to resolve the conversation and its fallback session).</param>
    /// <param name="sessions">The session store (source of the per-session history snapshots).</param>
    public ConversationHistoryAssembler(IConversationStore conversations, ISessionStore sessions)
    {
        _conversations = conversations;
        _sessions = sessions;
    }

    /// <inheritdoc />
    public async Task<ConversationHistoryResponse?> AssembleAsync(
        ConversationId conversationId,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetAsync(conversationId, cancellationToken);
        if (conversation is null)
            return null;

        // Get all sessions belonging to this conversation, ordered by CreatedAt ascending.
        // ListByConversationAsync guarantees Active+Sealed inclusion + the ordering contract,
        // and goes through the indexed Sqlite path -- no full-table scan (F-7).
        var linkedSessions = await _sessions.ListByConversationAsync(conversationId, cancellationToken: cancellationToken);

        // Fallback for #732: cron sessions and sessions created before the conversation-linkage
        // migration may have conversation_id = NULL in the sessions table. ListByConversationAsync
        // filters on that column and returns nothing, leaving the history endpoint empty even
        // though the conversation has messages. When the indexed query returns no sessions,
        // fall back to loading conversation.ActiveSessionId directly - provided it is not already
        // included in the linked set (dedup guard).
        if (linkedSessions.Count == 0 && conversation.ActiveSessionId is { } fallbackSessionId)
        {
            var fallbackSession = await _sessions.GetAsync(fallbackSessionId, cancellationToken);
            if (fallbackSession is not null)
                linkedSessions = [fallbackSession];
        }

        var allEntries = AssembleEntries(linkedSessions);

        var totalCount = allEntries.Count;
        var page = PageFromNewest(allEntries, limit, offset);

        return new ConversationHistoryResponse(
            ConversationId: conversationId.Value,
            TotalCount: totalCount,
            Offset: offset,
            Limit: limit,
            Entries: page);
    }

    /// <summary>
    /// Flattens the ordered session list into a single chronological entry list, inserting a
    /// <c>boundary</c> marker between sessions, dropping <c>NO_REPLY</c> assistant turns (#773) and
    /// crash sentinels, and projecting compaction summaries as distinct <c>compaction</c> markers.
    /// Folded (<c>IsHistory</c>) entries are retained and flagged <c>IsFolded</c> (#2936) so the
    /// pre-compaction transcript stays reachable and can be rendered collapsed.
    /// </summary>
    private static List<ConversationHistoryEntry> AssembleEntries(IReadOnlyList<GatewaySession> linkedSessions)
    {
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
                    IsFolded = entry.IsHistory,
                    // #2149: project the orthogonal typed kind so live delivery and history replay
                    // agree; ResolveKind maps legacy/unstamped entries to "message".
                    MessageKind = entry.ResolveKind().Value
                });
            }
        }

        return allEntries;
    }

    /// <summary>
    /// Returns a page taken from the newest end of <paramref name="allEntries"/> so refreshes
    /// include the latest turns even when conversations have more than one page of history.
    /// </summary>
    private static List<ConversationHistoryEntry> PageFromNewest(
        IReadOnlyList<ConversationHistoryEntry> allEntries,
        int limit,
        int offset)
    {
        var totalCount = allEntries.Count;
        if (offset >= totalCount)
            return [];

        var take = Math.Min(limit, totalCount - offset);
        var endExclusive = totalCount - offset;
        var startIndex = Math.Max(0, endExclusive - take);

        // #2936 paging-cost mitigation. Unfolding pre-compaction history without this turns a
        // "cannot reach history" bug into a "reachable but unusable" one: at a 20-row page a 2,736-row
        // transcript is ~137 sequential round trips. Folded rows render collapsed, so when a page
        // lands inside a contiguous folded run we extend it backwards over the rest of that run (up
        // to MaxFoldedPageEntries) and hand the client the whole collapsed block in one response.
        // The extension only ever grows the page BACKWARDS from startIndex, so the newest-first
        // window the client already anchored on is unchanged and offset arithmetic stays monotonic:
        // the client advances its offset by the actual returned count.
        if (startIndex > 0 && allEntries[startIndex].IsFolded)
        {
            var maxStart = Math.Max(0, endExclusive - MaxFoldedPageEntries);
            while (startIndex > maxStart && allEntries[startIndex - 1].IsFolded)
                startIndex--;
        }

        return allEntries
            .Skip(startIndex)
            .Take(endExclusive - startIndex)
            .ToList();
    }
}
