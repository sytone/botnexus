using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Issue #2532: <c>GET /api/sessions</c> paged the STORE and then filtered by agent/status in the
/// controller, while the client advanced <c>offset</c> by the POST-filter row count. Two different
/// coordinate spaces: the walk crept forward one row at a time and only terminated by walking the
/// entire global session table (120+ blocking requests observed live, offsets 0..182 with deltas
/// decaying 6,6,6,4,3,3,...,1,1,1).
///
/// These tests assert the OBSERVABLE cost of the walk - the EXACT number of requests and the EXACT
/// offset sequence - not an internal flag. "More than one page was requested" would have passed
/// against the bug and proves nothing.
///
/// <para>
/// Rewritten from the #2499 version of this file, which asserted the then-correct contract that a
/// walk terminates on a trailing EMPTY page (offsets 0,50,100,107) because the endpoint reported no
/// total. That contract is gone: AC5 gives the response an explicit <c>hasMore</c>, so the trailing
/// probe is no longer required and asserting it would now pin a defect. The tests below assert the
/// same underlying property the originals were about - the client seeds the COMPLETE roster and the
/// walk terminates - against the new signal. No assertion was weakened: the request-count and
/// offset-sequence assertions here are strictly stronger than the originals.
/// </para>
/// </summary>
public sealed class SessionPagingTests
{
    /// <summary>Sessions belonging to the agent under test.</summary>
    private const int AgentSessions = 107;

    /// <summary>
    /// Sessions belonging to OTHER agents. M >> N is the whole point: under the old
    /// page-then-filter design these rows sat between the agent's rows in the store's coordinate
    /// space and were what the walk crawled through one row at a time.
    /// </summary>
    private const int ForeignSessions = 2000 - AgentSessions;

    /// <summary>
    /// A fake gateway that behaves like the FIXED endpoint: it filters by agent FIRST, then applies
    /// limit/offset to the filtered set, and reports totalCount/hasMore.
    /// </summary>
    /// <remarks>
    /// The server clamps <c>limit</c> to <paramref name="serverMaxLimit"/> regardless of what the
    /// client asks for (#2499's clamp trap). That is deliberate: it makes "returned fewer rows than
    /// requested" true on EVERY page, so any client that terminates on a short page terminates
    /// after page one and fails the roster assertion. Termination must come from <c>hasMore</c>.
    /// </remarks>
    private static List<(int? Limit, int Offset, string? AgentId, string? ConversationId)> StubPagedSessions(
        IGatewayRestClient restClient,
        string agentId,
        int agentSessions,
        int foreignSessions,
        int serverDefaultLimit = 50,
        int serverMaxLimit = 50,
        string? conversationIdForAll = null)
    {
        var requests = new List<(int? Limit, int Offset, string? AgentId, string? ConversationId)>();

        var owned = Enumerable.Range(0, agentSessions)
            .Select(i => Summary($"sess-{i:D4}", agentId, conversationIdForAll))
            .ToList();
        var foreign = Enumerable.Range(0, foreignSessions)
            .Select(i => Summary($"other-{i:D4}", $"other-agent-{i % 7}", conversationId: null))
            .ToList();
        var all = owned.Concat(foreign).ToList();

        restClient
            .GetSessionsAsync(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requestedAgent = call.ArgAt<string?>(0);
                var limit = call.ArgAt<int?>(1);
                var offset = call.ArgAt<int>(2);
                var conversationId = call.ArgAt<string?>(3);
                requests.Add((limit, offset, requestedAgent, conversationId));

                // FILTER FIRST. This is the fix: limit/offset address the filtered set.
                var matching = all
                    .Where(s => requestedAgent is null || s.AgentId == requestedAgent)
                    .Where(s => conversationId is null || s.ConversationId == conversationId)
                    .ToList();

                var effective = Math.Clamp(limit ?? serverDefaultLimit, 1, serverMaxLimit);
                var pageRows = matching.Skip(offset).Take(effective).ToList();

                return Task.FromResult(new SessionPageDto(
                    pageRows,
                    matching.Count,
                    offset + pageRows.Count < matching.Count));
            });

        return requests;
    }

    private static SessionSummary Summary(string sessionId, string agentId, string? conversationId)
        => new(
            SessionId: sessionId,
            AgentId: agentId,
            ChannelType: "portal",
            SessionType: "chat",
            Status: "Active",
            MessageCount: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ConversationId: conversationId);

    private static AgentInteractionService CreateInteractionService(IClientStateStore store, IGatewayRestClient restClient)
        => new(
            store,
            new GatewayHubConnection(),
            restClient,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentInteractionService>.Instance);

    // ── AC1/AC5: the walk costs ceil(N/pageSize) requests, on the filtered coordinate space ──

    /// <summary>
    /// AC1 + AC5 + AC6. With 107 sessions for the agent among 2,000 total and a server that clamps
    /// every page to 50, the complete roster must resolve in EXACTLY ceil(107/50) = 3 requests at
    /// offsets 0, 50, 100.
    /// </summary>
    /// <remarks>
    /// Under the pre-fix design this walk issued dozens of requests with a decaying offset delta,
    /// because the server paged 2,000 rows and the client advanced by however many of those 50
    /// happened to belong to this agent. Asserting the EXACT sequence is what makes this test
    /// capable of failing against that bug; asserting only "the roster is complete" would not,
    /// since the buggy walk did eventually find everything - just at 40x the cost.
    /// </remarks>
    [Fact]
    public async Task ConversationSelection_ResolvesAgentRoster_InExactlyOneRequestPerFilteredPage()
    {
        var store = new ClientStateStore();
        var restClient = Substitute.For<IGatewayRestClient>();
        var service = CreateInteractionService(store, restClient);

        store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent 1", IsConnected = true });
        var agent = store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Chat",
            HistoryLoaded = true
        };

        // Every one of the agent's sessions belongs to the selected conversation, so the
        // conversation-scoped read returns the agent's whole roster.
        var requests = StubPagedSessions(
            restClient, "agent-1", AgentSessions, ForeignSessions, conversationIdForAll: "conv-1");

        await service.SelectConversationAsync("agent-1", "conv-1");

        var registered = Enumerable.Range(0, AgentSessions)
            .Count(i => store.TryResolveAgentBySession($"sess-{i:D4}", out _));
        registered.ShouldBe(
            AgentSessions,
            $"the walk must seed the complete roster; it registered {registered} of {AgentSessions}.");

        // EXACT cost. ceil(107/50) == 3. No trailing empty probe: hasMore said so on page 3.
        requests.Count.ShouldBe(
            3,
            "the roster must resolve in ceil(N/pageSize) requests. A higher count means limit/offset " +
            "are addressing a different set than the client consumes (#2532), or that the walk is " +
            "probing for an empty page instead of trusting hasMore (#2532 AC5).");
        requests.Select(r => r.Offset).ShouldBe(new[] { 0, 50, 100 });

        // No foreign session may leak into this agent's registry.
        store.TryResolveAgentBySession("other-0000", out _).ShouldBeFalse(
            "filtering happens in the store; foreign rows must never reach the client.");
    }

    /// <summary>
    /// AC5 / the #2499 clamp trap, isolated. The server clamps every page to 50 while the client
    /// asks for 200, so <c>returned &lt; requested</c> is true on EVERY page including the last.
    /// A client that treats a short page as exhaustion stops after one page.
    /// </summary>
    [Fact]
    public async Task Walk_DoesNotTerminateOnShortPage_BecauseServerClampsLimit()
    {
        var store = new ClientStateStore();
        var restClient = Substitute.For<IGatewayRestClient>();

        var requests = StubPagedSessions(restClient, "agent-1", AgentSessions, ForeignSessions);

        var roster = await SessionRosterLoader.LoadAllAsync(restClient, "agent-1");

        roster.Count.ShouldBe(
            AgentSessions,
            "every page came back shorter than the requested 200 because the server clamped to 50; " +
            "terminating on a short page would have stopped at 50.");
        requests.ShouldAllBe(r => r.Limit == SessionRosterLoader.SessionPageSize);
        requests.Count.ShouldBe(3);
    }

    /// <summary>
    /// The walk must terminate on the exact-multiple boundary without an extra probe: when the last
    /// page exactly fills, <c>hasMore</c> is false and no further request is issued.
    /// </summary>
    [Fact]
    public async Task Walk_TerminatesOnExactPageBoundary_WithoutTrailingProbe()
    {
        var store = new ClientStateStore();
        var restClient = Substitute.For<IGatewayRestClient>();

        // 100 sessions at a 50-row server cap == exactly two full pages.
        var requests = StubPagedSessions(restClient, "agent-1", agentSessions: 100, foreignSessions: 500);

        var roster = await SessionRosterLoader.LoadAllAsync(restClient, "agent-1");

        roster.Count.ShouldBe(100);
        requests.Count.ShouldBe(
            2,
            "hasMore is false on the second page, so the trailing empty probe the pre-#2532 walk " +
            "needed must not be issued.");
        requests.Select(r => r.Offset).ShouldBe(new[] { 0, 50 });
    }

    // ── AC2: agent switch performs ZERO session requests ──

    /// <summary>
    /// AC2. Selecting an agent loads conversations ONLY. The old implementation fired a full
    /// session enumeration in parallel with the conversation list on every agent switch, which with
    /// the paging bug meant 120+ blocking requests per click.
    /// </summary>
    [Fact]
    public async Task AgentSwitch_PerformsZeroSessionRequests()
    {
        var store = new ClientStateStore();
        var restClient = Substitute.For<IGatewayRestClient>();
        var service = CreateInteractionService(store, restClient);

        store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent 1", IsConnected = true });

        restClient.GetConversationsAsync("agent-1")
            .Returns(new List<ConversationSummaryDto>());
        restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummaryDto>());

        var requests = StubPagedSessions(restClient, "agent-1", AgentSessions, ForeignSessions);

        await service.RefreshConversationsAsync("agent-1");

        requests.Count.ShouldBe(
            0,
            "selecting an agent must load conversations only (#2532 AC2); sessions are a " +
            "per-conversation concern and belong on conversation selection.");
        await restClient.Received(1).GetConversationsAsync("agent-1");
    }

    // ── AC3: conversation selection performs the session load ──

    /// <summary>
    /// AC3. Sessions for a conversation load when THAT conversation is selected, scoped to it, so
    /// the server returns only the relevant handful rather than the agent's whole history.
    /// </summary>
    [Fact]
    public async Task ConversationSelection_LoadsSessionsScopedToThatConversation()
    {
        var store = new ClientStateStore();
        var restClient = Substitute.For<IGatewayRestClient>();
        var service = CreateInteractionService(store, restClient);

        store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent 1", IsConnected = true });
        var agent = store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Chat",
            HistoryLoaded = true
        };

        var requests = StubPagedSessions(
            restClient, "agent-1", agentSessions: 3, foreignSessions: 500, conversationIdForAll: "conv-1");

        await service.SelectConversationAsync("agent-1", "conv-1");

        requests.Count.ShouldBe(1, "three sessions fit in one page, so one request suffices.");
        requests[0].AgentId.ShouldBe("agent-1");
        requests[0].ConversationId.ShouldBe(
            "conv-1",
            "the read must be scoped to the selected conversation, not the whole agent (#2532 AC3).");

        Enumerable.Range(0, 3)
            .Count(i => store.TryResolveAgentBySession($"sess-{i:D4}", out _))
            .ShouldBe(3, "selecting a conversation must register its sessions.");
    }

    // ── Portal bootstrap still seeds the full (unfiltered) roster ──

    /// <summary>
    /// Portal bootstrap loads the global roster once. The walk is the same shared one, so it must
    /// show the same exact-cost property against the unfiltered set.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_SeedsCompleteSessionRoster_UsingHasMoreToTerminate()
    {
        var store = new ClientStateStore();
        var restClient = Substitute.For<IGatewayRestClient>();
        var service = new PortalLoadService(
            restClient,
            new GatewayHubConnection(),
            store,
            Substitute.For<IGatewayEventHandler>());

        restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-1", "Test Agent")]);
        restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummaryDto>());

        // No foreign rows here: bootstrap requests agentId: null, so the whole store IS the
        // filtered set and 107 rows at a 50-row cap is ceil(107/50) == 3 requests.
        var requests = StubPagedSessions(restClient, "agent-1", AgentSessions, foreignSessions: 0);

        await service.InitializeAsync("http://localhost:5000/hub/gateway");

        var registered = Enumerable.Range(0, AgentSessions)
            .Count(i => store.TryResolveAgentBySession($"sess-{i:D4}", out _));
        registered.ShouldBe(AgentSessions);

        requests.Count.ShouldBe(3);
        requests.Select(r => r.Offset).ShouldBe(new[] { 0, 50, 100 });
        requests.ShouldAllBe(r => r.AgentId == null, "bootstrap loads the global roster.");
    }
}
