namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// The single implementation of the <c>GET /api/sessions</c> paging walk (#2452, #2532).
/// </summary>
/// <remarks>
/// This existed twice - byte-identical copies in <c>PortalLoadService</c> and
/// <c>AgentInteractionService</c> - which meant every bug in it had to be found and fixed twice,
/// and #2532 is exactly the failure that pattern produces: the walk advanced <c>offset</c> by the
/// number of rows it received while the server was paging a differently-filtered set, so it crept
/// forward one row at a time and only terminated by walking the entire global session table.
/// <para>
/// Two rules make that unrepresentable here:
/// </para>
/// <list type="number">
/// <item>Termination is driven by the server's <c>hasMore</c> flag, never by a short page. The
/// server clamps <c>limit</c> to its own maximum (#2499), so "returned fewer than requested" is
/// indistinguishable from "was clamped" and proves nothing about exhaustion.</item>
/// <item>The offset advances by the number of rows RECEIVED. That is correct only because #2532
/// moved the agent/status/conversation predicate into the store: the server now pages exactly the
/// set the client is accumulating, so the client's running count and the server's offset are the
/// same coordinate. It is also the only advance that survives the clamp - stepping by the
/// requested page size would skip every row the server trimmed off a clamped page.</item>
/// </list>
/// </remarks>
public static class SessionRosterLoader
{
    /// <summary>
    /// Page size requested when walking <c>GET /api/sessions</c>. Matches the endpoint's hard cap
    /// (#2411/#2468) so the walk uses the fewest possible round trips.
    /// </summary>
    public const int SessionPageSize = 200;

    /// <summary>
    /// Hard stop so a misbehaving server that always reports <c>hasMore</c> can never spin the
    /// portal forever. 200 * 200 = 40,000 sessions, far beyond any real store.
    /// </summary>
    public const int MaxSessionPages = 200;

    /// <summary>
    /// Reads every session page matching the given filter and returns the complete roster.
    /// </summary>
    /// <param name="restClient">Gateway REST client.</param>
    /// <param name="agentId">Restrict to one agent, or <c>null</c> for all agents.</param>
    /// <param name="conversationId">Restrict to one conversation, or <c>null</c> for all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<List<SessionSummary>> LoadAllAsync(
        IGatewayRestClient restClient,
        string? agentId = null,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restClient);

        var all = new List<SessionSummary>();

        for (var page = 0; page < MaxSessionPages; page++)
        {
            var result = await restClient.GetSessionsAsync(
                agentId,
                SessionPageSize,
                all.Count,
                conversationId,
                cancellationToken);

            all.AddRange(result.Sessions);

            // hasMore is authoritative (#2532 AC5). Do NOT substitute a short-page test here.
            if (!result.HasMore)
                break;

            // Defensive: a server that reports hasMore while returning nothing would otherwise
            // spin at a fixed offset until MaxSessionPages. Stop immediately instead.
            if (result.Sessions.Count == 0)
                break;
        }

        return all;
    }
}
