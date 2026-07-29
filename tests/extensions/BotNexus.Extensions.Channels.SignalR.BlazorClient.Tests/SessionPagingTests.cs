using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Regression tests for #2499: <c>GET /api/sessions</c> became paged (default 50, max 200) in
/// #2411/#2468 and still returns a bare JSON array, so a portal consumer that issues a single
/// request silently seeds an incomplete session registry.
///
/// These tests assert the OBSERVABLE - the number of sessions actually registered in the client
/// store - rather than that a paging parameter was passed. A consumer that stops after the first
/// page registers only that page and fails here.
/// </summary>
public sealed class SessionPagingTests
{
    /// <summary>
    /// Total sessions the fake server holds. The fake caps every page at 50 - the real endpoint's
    /// default - regardless of the limit the client asks for, so 107 sessions require exactly three
    /// round trips (50 @ 0, 50 @ 50, 7 @ 100) and a non-paging consumer sees only the first 50.
    /// </summary>
    private const int TotalSessions = 107;

    /// <summary>
    /// Installs a fake <c>GET /api/sessions</c> on the substitute that behaves like the real paged
    /// endpoint: it honours <c>limit</c> (defaulting to the server's 50) and <c>offset</c>, and
    /// returns a bare array that is short on the final page.
    /// </summary>
    private static List<(int? Limit, int Offset)> StubPagedSessions(
        IGatewayRestClient restClient,
        string agentId,
        int total,
        int serverDefaultLimit = 50,
        int serverMaxLimit = 50)
    {
        var requests = new List<(int? Limit, int Offset)>();
        var all = Enumerable.Range(0, total)
            .Select(i => new SessionSummary(
                SessionId: $"sess-{i:D4}",
                AgentId: agentId,
                ChannelType: "portal",
                SessionType: "chat",
                Status: "Active",
                MessageCount: 0,
                CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-i),
                UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-i)))
            .ToList();

        restClient
            .GetSessionsAsync(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var limit = call.ArgAt<int?>(1);
                var offset = call.ArgAt<int>(2);
                requests.Add((limit, offset));

                var effective = Math.Clamp(limit ?? serverDefaultLimit, 1, serverMaxLimit);
                return Task.FromResult<IReadOnlyList<SessionSummary>>(
                    all.Skip(offset).Take(effective).ToList());
            });

        return requests;
    }

    /// <summary>
    /// Portal bootstrap must seed the COMPLETE session roster. With 107 sessions behind a paged
    /// endpoint, a single-request implementation registers at most one page.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_SeedsCompleteSessionRoster_WhenServerPagesTheResponse()
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

        var requests = StubPagedSessions(restClient, "agent-1", TotalSessions);

        await service.InitializeAsync("http://localhost:5000/hub/gateway");

        var registered = Enumerable.Range(0, TotalSessions)
            .Count(i => store.TryResolveAgentBySession($"sess-{i:D4}", out _));

        registered.ShouldBe(
            TotalSessions,
            "PortalLoadService must page GET /api/sessions until a short page is returned; " +
            $"it registered {registered} of {TotalSessions} sessions.");

        // Paging must advance the offset monotonically and stop on the short page.
        // 50 @ 0, 50 @ 50, 7 @ 100, then the trailing empty probe at 107 that proves exhaustion.
        requests.Select(r => r.Offset).ShouldBe(new[] { 0, 50, 100, 107 });
    }

    /// <summary>
    /// Per-agent refresh has the same unbounded assumption at
    /// <c>AgentInteractionService.RefreshConversationsForAgentAsync</c>: an agent with more
    /// sessions than the page size must still end up fully registered.
    /// </summary>
    [Fact]
    public async Task RefreshConversationsAsync_SeedsCompleteSessionRoster_WhenServerPagesTheResponse()
    {
        var store = new ClientStateStore();
        var restClient = Substitute.For<IGatewayRestClient>();
        var service = new AgentInteractionService(
            store,
            new GatewayHubConnection(),
            restClient,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentInteractionService>.Instance);

        store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent 1", IsConnected = true });

        restClient.GetConversationsAsync("agent-1")
            .Returns(new List<ConversationSummaryDto>());
        restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummaryDto>());

        var requests = StubPagedSessions(restClient, "agent-1", TotalSessions);

        await service.RefreshConversationsAsync("agent-1");

        var registered = Enumerable.Range(0, TotalSessions)
            .Count(i => store.TryResolveAgentBySession($"sess-{i:D4}", out _));

        registered.ShouldBe(
            TotalSessions,
            "RefreshConversationsForAgentAsync must page GET /api/sessions until a short page is " +
            $"returned; it registered {registered} of {TotalSessions} sessions.");

        requests.ShouldAllBe(r => r.Limit != null);
        requests.Select(r => r.Offset).ShouldBe(new[] { 0, 50, 100, 107 });
    }

    /// <summary>
    /// The paged loop must terminate on the exact-multiple boundary too: when the last full page
    /// is followed by an empty page, the consumer must stop rather than spin.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_TerminatesOnExactPageBoundary()
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

        // 100 == exactly two full pages at the 50 server cap, so the walk only stops via the
        // trailing empty page - the boundary a naive "stop on short page" loop would get wrong.
        var requests = StubPagedSessions(restClient, "agent-1", 100);

        await service.InitializeAsync("http://localhost:5000/hub/gateway");

        var registered = Enumerable.Range(0, 100)
            .Count(i => store.TryResolveAgentBySession($"sess-{i:D4}", out _));

        registered.ShouldBe(100);
        requests.Count.ShouldBeLessThan(10, "the paging loop must terminate, not spin.");
    }
}
