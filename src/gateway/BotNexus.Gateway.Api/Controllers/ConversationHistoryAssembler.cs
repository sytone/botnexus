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

        var allEntries = ConversationHistoryProjection.Project(linkedSessions);

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
