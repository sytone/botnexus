namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Merges a freshly fetched server history page into the transcript currently displayed, so a
/// message lost in transit over SignalR reappears after a refresh (#3846).
/// </summary>
/// <remarks>
/// <para>
/// <b>Insert-only, by design.</b> The reconciler never removes a locally held row. The refresh
/// fetches the most-recent page only, so rows the client paged in by scrolling up are legitimately
/// absent from that page; treating "absent from the server page" as "deleted on the server" would
/// silently truncate the transcript the user is reading. The defect this repairs is a HOLE, and the
/// only safe repair for a hole is an insert.
/// </para>
/// <para>
/// <b>Identity.</b> Persisted rows use <see cref="ChatMessage.ServerEntryId"/> and tool rows use
/// <see cref="ChatMessage.ToolCallId"/>. A live non-tool row has neither until refresh; it is paired
/// once, in order, with a compatible persisted row by kind, role, boundary session and content.
/// Server multiplicity is retained, so two genuinely distinct identical messages remain two rows.
/// </para>
/// <para>
/// Shared by mobile and desktop through <c>PortalLoadService.RefreshAsync</c> - there is deliberately
/// no second reconciliation implementation (#3846 clause 7).
/// </para>
/// </remarks>
public static class TranscriptReconciler
{
    /// <summary>
    /// Returns the local timeline with every row from <paramref name="server"/> that is missing
    /// locally inserted at its chronological position.
    /// </summary>
    /// <param name="local">The timeline currently displayed.</param>
    /// <param name="server">The freshly fetched server page, in ascending timestamp order.</param>
    /// <returns>The reconciled timeline. Idempotent: reconciling an already-complete transcript
    /// returns the same rows in the same order.</returns>
    public static IReadOnlyList<ChatMessage> Reconcile(
        IReadOnlyList<ChatMessage> local,
        IReadOnlyList<ChatMessage> server)
    {
        var merged = new List<ChatMessage>(local);
        foreach (var candidate in MissingRows(local, server))
            merged.Insert(InsertionIndexFor(merged, candidate.Timestamp), candidate);

        return merged;
    }

    /// <summary>
    /// Counts how many rows of <paramref name="server"/> are absent from <paramref name="local"/>,
    /// using the same identity the merge uses. Lets a caller advance its paging counters by exactly
    /// the number of rows a reconcile inserted without diffing the two lists itself.
    /// </summary>
    public static int CountMissing(IReadOnlyList<ChatMessage> local, IReadOnlyList<ChatMessage> server) =>
        MissingRows(local, server).Count;

    private static IReadOnlyList<ChatMessage> MissingRows(
        IReadOnlyList<ChatMessage> local,
        IReadOnlyList<ChatMessage> server)
    {
        var unmatchedLocal = new List<ChatMessage>(local);
        var seenServerKeys = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<ChatMessage>();

        foreach (var candidate in server)
        {
            var serverKey = StableKeyOf(candidate);
            if (serverKey is not null && !seenServerKeys.Add(serverKey))
                continue;

            var match = unmatchedLocal.FindIndex(localRow => RowsMatch(localRow, candidate));
            if (match >= 0)
            {
                unmatchedLocal.RemoveAt(match);
                continue;
            }

            missing.Add(candidate);
        }

        return missing;
    }

    private static bool RowsMatch(ChatMessage local, ChatMessage server)
    {
        var localStableKey = StableKeyOf(local);
        var serverStableKey = StableKeyOf(server);
        if (localStableKey is not null && serverStableKey is not null)
            return string.Equals(localStableKey, serverStableKey, StringComparison.Ordinal);

        return CompatibleKeyOf(local) == CompatibleKeyOf(server);
    }

    // The first position whose timestamp is strictly LATER than the candidate's. Rows sharing a
    // timestamp therefore land after the ones already present, preserving burst order.
    private static int InsertionIndexFor(List<ChatMessage> merged, DateTimeOffset timestamp)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Timestamp > timestamp)
                return i;
        }

        return merged.Count;
    }

    private static string? StableKeyOf(ChatMessage message)
    {
        // Tool calls already have a stable identity shared by live and REST rows; prefer it even
        // after the REST projection also supplies a transcript entry id.
        if (!string.IsNullOrEmpty(message.ToolCallId))
            return $"tool\u001f{message.ToolCallId}";

        if (!string.IsNullOrEmpty(message.ServerEntryId))
            return $"entry\u001f{message.ServerEntryId}";

        return null;
    }

    private static string CompatibleKeyOf(ChatMessage message) => string.Join(
        '\u001f',
        message.Kind,
        message.Role,
        message.BoundarySessionId ?? string.Empty,
        message.Content);
}
