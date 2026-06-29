using System.Net;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class PortalLoadServiceTests
{
    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly IGatewayEventHandler _eventHandler = Substitute.For<IGatewayEventHandler>();
    private readonly GatewayHubConnection _hub = new();
    private readonly PortalLoadService _service;

    public PortalLoadServiceTests()
    {
        _service = new PortalLoadService(_restClient, _hub, _store, _eventHandler);
    }

    /// <summary>
    /// The client kind defaults to "desktop" so a portal that never sets it keeps the
    /// historical desktop-portal behaviour (#1209 AC#5).
    /// </summary>
    [Fact]
    public void ClientKind_DefaultsToDesktop()
    {
        _service.ClientKind.ShouldBe("desktop");
    }

    /// <summary>
    /// The client kind is settable so the per-app caller (desktop portal vs mobile app) can
    /// declare its device class before InitializeAsync forwards it to the hub connection (#1209 AC#1).
    /// </summary>
    [Fact]
    public void ClientKind_IsSettable()
    {
        _service.ClientKind = "mobile";
        _service.ClientKind.ShouldBe("mobile");
    }

    /// <summary>
    /// Reproduces the exact bug: a stale cron-session projection whose backing session
    /// returns 404 from GetSessionHistoryAsync must NOT abort portal initialization.
    /// Before the fix, this threw HttpRequestException 404 and set LoadError, blocking
    /// all agents and conversations from loading.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_stale_cron_session_history_404_does_not_abort_initialization()
    {
        // Arrange: one agent with a cron session that will 404 on history
        var staleCronSessionId = "cron:20260509002033:6f2f84a4f1634ff492a4fec212872c54";

        _restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-1", "Test Agent")]);

        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummaryDto>());

        _restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SessionSummary>
            {
                new(
                    SessionId: staleCronSessionId,
                    AgentId: "agent-1",
                    ChannelType: "cron",
                    SessionType: "cron",
                    Status: "Active",
                    MessageCount: 0,
                    CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
                    UpdatedAt: DateTimeOffset.UtcNow.AddDays(-1))
            });

        // The key: session history returns 404 for the deleted cron session
        _restClient.GetSessionHistoryAsync(staleCronSessionId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<SessionHistoryResponseDto?>(_ =>
                throw new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        // Hub connect will throw since we have no real hub — but the test validates
        // that we get PAST the history loading. We override ConnectAsync behavior by
        // catching the exception in the outer try block (which sets LoadError to a hub error, not a 404).
        // Actually: _hub.ConnectAsync will throw NullReferenceException or similar.
        // To isolate, let's verify the stale projection was removed before hub connect.

        // Act
        await _service.InitializeAsync("http://localhost:5000/hub/gateway");

        // Assert: the service should not have a 404-related error.
        // It may have a hub connection error (since we're not mocking the real hub),
        // but the critical thing is the 404 did NOT abort initialization.
        // The stale cron projection should have been removed from the store.
        var agent = _store.GetAgent("agent-1");
        Assert.NotNull(agent);

        // The stale virtual cron conversation must be removed after 404
        var cronConvId = $"cron-session:{staleCronSessionId}";
        Assert.False(agent.Conversations.ContainsKey(cronConvId),
            "Stale cron-session projection should be removed after 404.");

        // If there's a LoadError, it should NOT mention "404" or "Not Found" — that would
        // indicate the 404 leaked to the top-level catch.
        if (_service.LoadError is not null)
        {
            Assert.DoesNotContain("404", _service.LoadError);
            Assert.DoesNotContain("Not Found", _service.LoadError);
        }
    }

    /// <summary>
    /// Ensures that a real (non-virtual) conversation 404 during initial history load
    /// is also handled gracefully — logged but not fatal to initialization.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_real_conversation_history_404_does_not_abort_initialization()
    {
        _restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-1", "Test Agent")]);

        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummaryDto>
            {
                new("conv-1", "agent-1", "Chat", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            });

        _restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SessionSummary>());

        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ConversationHistoryResponseDto?>(_ =>
                throw new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        // Act
        await _service.InitializeAsync("http://localhost:5000/hub/gateway");

        // Assert: the conversation still exists (not removed — only virtual crons get removed)
        var agent = _store.GetAgent("agent-1");
        Assert.NotNull(agent);
        Assert.True(agent.Conversations.ContainsKey("conv-1"));

        // LoadError should not mention 404
        if (_service.LoadError is not null)
        {
            Assert.DoesNotContain("404", _service.LoadError);
            Assert.DoesNotContain("Not Found", _service.LoadError);
        }
    }

    /// <summary>
    /// Regression for the agent-description-not-showing bug: the REST seed path in
    /// <see cref="PortalLoadService.InitializeAsync(string, System.Threading.CancellationToken)"/>
    /// must copy <c>Description</c> from the agent summary into <c>AgentState</c>. The
    /// agent panel header renders <c>AgentState.Description</c>, and the initial portal
    /// load seeds from REST (not the SignalR broadcast), so omitting it left the header
    /// description blank even when the agent had one in config.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_SeedsAgentDescriptionFromRestSummary()
    {
        _restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-1", "Beacon", Emoji: "\U0001F4E1", Description: "Signals and situational awareness", IsBuiltIn: false)]);
        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummaryDto>());
        _restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SessionSummary>());

        await _service.InitializeAsync("http://localhost:5000/hub/gateway");

        var agent = _store.GetAgent("agent-1");
        Assert.NotNull(agent);
        Assert.Equal("Signals and situational awareness", agent.Description);
        Assert.True(agent.IsBuiltIn == false);
    }

    [Fact]
    public async Task InitializeAsync_RefreshesStoreFromApiInsteadOfUsingPreexistingConversationState()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Stale Agent",
            IsConnected = true
        });
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                "stale-conv",
                "agent-1",
                "Stale conversation",
                true,
                "Active",
                null,
                0,
                DateTimeOffset.UtcNow.AddHours(-2),
                DateTimeOffset.UtcNow.AddHours(-1))
        ]);

        _restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-1", "General Assistant")]);
        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns([
                new ConversationSummaryDto(
                    "fresh-conv",
                    "agent-1",
                    "Fresh conversation",
                    true,
                    "Active",
                    "s-fresh",
                    0,
                    DateTimeOffset.UtcNow.AddMinutes(-15),
                    DateTimeOffset.UtcNow.AddMinutes(-1))
            ]);
        _restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _restClient.GetHistoryAsync("fresh-conv", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationHistoryResponseDto("fresh-conv", 0, 0, 200, []));

        await _service.InitializeAsync("http://localhost:5000/hub/gateway");

        await _restClient.Received(1).GetConversationsAsync("agent-1", Arg.Any<CancellationToken>());
        await _restClient.Received(1).GetHistoryAsync("fresh-conv", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        var agent = _store.GetAgent("agent-1");
        Assert.NotNull(agent);
        Assert.True(agent.Conversations.ContainsKey("fresh-conv"));
        Assert.False(agent.Conversations.ContainsKey("stale-conv"));
    }
}
