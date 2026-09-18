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
        var aligned = AlignServerRows(local, server);
        for (var serverIndex = 0; serverIndex < aligned.Count; serverIndex++)
        {
            var row = aligned[serverIndex];
            if (row.LocalRow is not null)
                continue;

            merged.Insert(InsertionIndexFor(merged, aligned, serverIndex), row.ServerRow);
            aligned[serverIndex] = row with { LocalRow = row.ServerRow };
        }

        ApplyAuthoritativeToolCompletions(merged, server);
        return merged;
    }

    /// <summary>
    /// Counts how many rows of <paramref name="server"/> are absent from <paramref name="local"/>,
    /// using the same identity the merge uses. Lets a caller advance its paging counters by exactly
    /// the number of rows a reconcile inserted without diffing the two lists itself.
    /// </summary>
    public static int CountMissing(IReadOnlyList<ChatMessage> local, IReadOnlyList<ChatMessage> server) =>
        AlignServerRows(local, server).Count(row => row.LocalRow is null);

    private static List<AlignedServerRow> AlignServerRows(
        IReadOnlyList<ChatMessage> local,
        IReadOnlyList<ChatMessage> server)
    {
        var unmatchedLocal = new List<ChatMessage>(local);
        var seenServerKeys = new HashSet<string>(StringComparer.Ordinal);
        var aligned = new List<AlignedServerRow>(server.Count);

        foreach (var candidate in server)
        {
            var serverKey = StableKeyOf(candidate);
            if (serverKey is not null && !seenServerKeys.Add(serverKey))
                continue;

            var match = unmatchedLocal.FindIndex(localRow => RowsMatch(localRow, candidate));
            var localRow = match >= 0 ? unmatchedLocal[match] : null;
            if (match >= 0)
                unmatchedLocal.RemoveAt(match);

            aligned.Add(new AlignedServerRow(candidate, localRow));
        }

        return aligned;
    }

    private static bool RowsMatch(ChatMessage local, ChatMessage server)
    {
        var localStableKey = StableKeyOf(local);
        var serverStableKey = StableKeyOf(server);
        if (localStableKey is not null && serverStableKey is not null)
            return string.Equals(localStableKey, serverStableKey, StringComparison.Ordinal);

        return CompatibleKeyOf(local) == CompatibleKeyOf(server);
    }

    private static void ApplyAuthoritativeToolCompletions(
        List<ChatMessage> merged,
        IReadOnlyList<ChatMessage> server)
    {
        var seenToolCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in server)
        {
            if (string.IsNullOrEmpty(candidate.ToolCallId)
                || candidate.ToolResult is null
                || !seenToolCalls.Add(candidate.ToolCallId))
            {
                continue;
            }

            var index = merged.FindIndex(message =>
                string.Equals(message.ToolCallId, candidate.ToolCallId, StringComparison.Ordinal));
            if (index < 0 || merged[index].ToolResult is not null)
                continue;

            // Keep the client row identity so render caches and ToolStart bookkeeping remain stable,
            // while the persisted row supplies the authoritative terminal payload and server identity.
            merged[index] = candidate with { Id = merged[index].Id };
        }
    }

    private static int InsertionIndexFor(
        List<ChatMessage> merged,
        IReadOnlyList<AlignedServerRow> aligned,
        int serverIndex)
    {
        var timestamp = aligned[serverIndex].ServerRow.Timestamp;

        // A represented successor at the same timestamp is authoritative evidence that this hole
        // belongs before it. Prefer that anchor over timestamp-only insertion so [B] reconciled
        // against server [A, B] becomes [A, B], not [B, A].
        for (var i = serverIndex + 1; i < aligned.Count; i++)
        {
            if (aligned[i].ServerRow.Timestamp != timestamp)
                break;

            var successor = aligned[i].LocalRow;
            if (successor is not null)
                return ReferenceIndexOf(merged, successor);
        }

        // With no represented successor, append after the nearest represented server predecessor
        // in the same timestamp burst. This retains the server's suffix order while leaving local
        // rows outside the fetched page untouched.
        for (var i = serverIndex - 1; i >= 0; i--)
        {
            if (aligned[i].ServerRow.Timestamp != timestamp)
                break;

            var predecessor = aligned[i].LocalRow;
            if (predecessor is not null)
                return ReferenceIndexOf(merged, predecessor) + 1;
        }

        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Timestamp > timestamp)
                return i;
        }

        return merged.Count;
    }

    private static int ReferenceIndexOf(List<ChatMessage> rows, ChatMessage target)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], target))
                return i;
        }

        throw new InvalidOperationException("An aligned transcript row was not present in the merged timeline.");
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

    private sealed record AlignedServerRow(ChatMessage ServerRow, ChatMessage? LocalRow);
}
