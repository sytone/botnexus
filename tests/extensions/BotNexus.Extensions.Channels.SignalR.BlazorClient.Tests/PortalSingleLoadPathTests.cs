using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Pins the SINGLE load path for portal session state (#2541 AC1).
/// </summary>
/// <remarks>
/// Before #2541 the portal seeded its session roster twice: once from the REST
/// <c>GET /api/sessions</c> paging walk, and again from the <c>Sessions</c> payload
/// <c>SubscribeAll</c> returns over the hub. Two writers into the same client store with no
/// ordering guarantee between them — not harmless redundancy, since the two snapshots are taken at
/// different instants and whichever landed last won.
/// <para>
/// Jon's 2026-07-29 decision keeps portal LOAD on REST and leaves SignalR as the notification
/// channel, so the hub payload is the redundant one and is now discarded.
/// </para>
/// <para>
/// <b>Why these are behavioural and the strict one is structural.</b> A unit test here cannot
/// force <c>SubscribeAll</c> to return a hub-only session: <c>GatewayHubConnection</c> is sealed
/// with no injectable seam, so an unconnected hub throws and BOTH the fixed and the broken code
/// register nothing — a mock blind spot exactly the width of the defect. The load-path rule is
/// therefore pinned structurally by <c>PortalLoadPathArchitectureTests</c>; what these tests add is
/// that the REST walk genuinely still seeds the roster on every path, so the fence cannot be
/// satisfied by deleting the load instead of the duplicate.
/// </para>
/// </remarks>
public sealed class PortalSingleLoadPathTests
{
    private static SessionSummary Summary(string sessionId, string agentId)
        => new(
            SessionId: sessionId,
            AgentId: agentId,
            ChannelType: "portal",
            SessionType: "chat",
            Status: "Active",
            MessageCount: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ConversationId: null);

    private static IGatewayRestClient StubRest(params SessionSummary[] sessions)
    {
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-a", "Agent A")]);
        restClient.GetConversationsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummaryDto>());
        restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SessionPageDto([.. sessions], TotalCount: sessions.Length, HasMore: false));
        return restClient;
    }

    /// <summary>
    /// Happy path: the REST roster walk seeds the store during initialize, and it is the surviving
    /// load path after the hub writer was removed.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_SeedsSessionRosterFromRest()
    {
        var store = new ClientStateStore();
        var restClient = StubRest(Summary("rest-session", "agent-a"));

        var service = new PortalLoadService(
            restClient,
            new GatewayHubConnection(),
            store,
            Substitute.For<IGatewayEventHandler>());

        await service.InitializeAsync("http://localhost:5000/hub/gateway");

        var agent = store.GetAgent("agent-a").ShouldNotBeNull();
        agent.AgentId.ShouldBe("agent-a");
        store.TryResolveAgentBySession("rest-session", out var resolved).ShouldBeTrue();
        resolved.ShouldBe("agent-a");
    }

    /// <summary>
    /// Both REST rows are registered — the walk seeds the COMPLETE roster, not just the first row.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_RegistersEveryRestRosterRow()
    {
        var store = new ClientStateStore();
        var restClient = StubRest(Summary("rest-a", "agent-a"), Summary("rest-b", "agent-a"));

        var service = new PortalLoadService(
            restClient,
            new GatewayHubConnection(),
            store,
            Substitute.For<IGatewayEventHandler>());

        await service.InitializeAsync("http://localhost:5000/hub/gateway");

        store.TryResolveAgentBySession("rest-a", out _).ShouldBeTrue();
        store.TryResolveAgentBySession("rest-b", out _).ShouldBeTrue();
    }

    /// <summary>
    /// Sad path: removing the hub writer must not have removed the LOAD. <c>RefreshAsync</c> now
    /// owns a REST roster re-walk of its own; before #2541 the only session refresh on that path
    /// came from the hub payload, so deleting that payload without adding this walk would have made
    /// refresh silently stop updating sessions while every fence stayed green.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ReWalksTheRestSessionRoster()
    {
        var store = new ClientStateStore();
        var restClient = StubRest(Summary("rest-initial", "agent-a"));

        var service = new PortalLoadService(
            restClient,
            new GatewayHubConnection(),
            store,
            Substitute.For<IGatewayEventHandler>());

        await service.InitializeAsync("http://localhost:5000/hub/gateway");

        // A session that appears only AFTER the initial load must be picked up by the refresh's own
        // REST walk.
        restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SessionPageDto(
                [Summary("rest-initial", "agent-a"), Summary("rest-late", "agent-a")],
                TotalCount: 2,
                HasMore: false));

        await service.RefreshAsync();

        store.TryResolveAgentBySession("rest-late", out var lateAgent).ShouldBeTrue(
            "RefreshAsync must re-walk the REST session roster now that the hub payload is no longer written (#2541 AC1)");
        lateAgent.ShouldBe("agent-a");
    }
}
