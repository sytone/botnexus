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
    /// folded entries, and projecting compaction summaries as distinct <c>compaction</c> markers.
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
                // Skip folded entries that have been superseded by compaction summaries.
                if (entry.IsHistory)
                    continue;

                if (entry.Role == MessageRole.Assistant &&
                    string.Equals(entry.Content?.Trim(), "NO_REPLY", StringComparison.Ordinal))
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
                        Content = entry.Content
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
        var startIndex = Math.Max(0, totalCount - offset - take);
        return allEntries
            .Skip(startIndex)
            .Take(take)
            .ToList();
    }
}
