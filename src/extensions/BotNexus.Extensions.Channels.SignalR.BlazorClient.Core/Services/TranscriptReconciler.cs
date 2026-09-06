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
/// <b>Identity.</b> <see cref="ChatMessage.Id"/> is client-minted per row (a fresh GUID on every
/// deserialisation), so it cannot identify the same logical message across two fetches. A tool row
/// is keyed by its server-assigned <see cref="ChatMessage.ToolCallId"/>; everything else is keyed by
/// its (kind, timestamp, role, content) tuple, which is stable across fetches while still keeping
/// two genuinely distinct rows that happen to share a timestamp apart.
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
        if (server.Count == 0)
            return merged;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in merged)
            seen.Add(KeyOf(message));

        foreach (var candidate in server)
        {
            if (!seen.Add(KeyOf(candidate)))
                continue;

            merged.Insert(InsertionIndexFor(merged, candidate.Timestamp), candidate);
        }

        return merged;
    }

    /// <summary>
    /// Counts how many rows of <paramref name="server"/> are absent from <paramref name="local"/>,
    /// using the same identity the merge uses. Lets a caller advance its paging counters by exactly
    /// the number of rows a reconcile inserted without diffing the two lists itself.
    /// </summary>
    public static int CountMissing(IReadOnlyList<ChatMessage> local, IReadOnlyList<ChatMessage> server)
    {
        var seen = new HashSet<string>(local.Select(KeyOf), StringComparer.Ordinal);
        return server.Count(m => !seen.Contains(KeyOf(m)));
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

    private static string KeyOf(ChatMessage message)
    {
        // A tool row's server-assigned call id is the only stable identity across the live
        // SignalR rendering and the REST re-fetch, whose Content differs (live streamed text vs
        // the stripped stored result).
        if (!string.IsNullOrEmpty(message.ToolCallId))
            return $"tool\u001f{message.ToolCallId}";

        return string.Join(
            '\u001f',
            message.Kind,
            message.Timestamp.UtcDateTime.ToString("O"),
            message.Role,
            message.BoundarySessionId ?? string.Empty,
            message.Content);
    }
}
