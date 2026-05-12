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
    /// Verifies that when a virtual cron session loads successfully, its history
    /// uses the session history endpoint (not conversation history).
    /// </summary>
    [Fact]
    public async Task InitializeAsync_virtual_cron_session_uses_session_history_endpoint()
    {
        var cronSessionId = "cron:20260510120000:abc123";

        _restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-1", "Test Agent")]);

        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummaryDto>());

        _restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SessionSummary>
            {
                new(
                    SessionId: cronSessionId,
                    AgentId: "agent-1",
                    ChannelType: "cron",
                    SessionType: "cron",
                    Status: "Active",
                    MessageCount: 0,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow)
            });

        _restClient.GetSessionHistoryAsync(cronSessionId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SessionHistoryResponseDto(0, 200, 1,
            [
                new SessionHistoryEntryDto
                {
                    Role = "assistant",
                    Content = "Cron executed successfully",
                    Timestamp = DateTimeOffset.UtcNow
                }
            ]));

        // Act
        await _service.InitializeAsync("http://localhost:5000/hub/gateway");

        // Assert: session history endpoint was called (not conversation history)
        await _restClient.Received(1).GetSessionHistoryAsync(cronSessionId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _restClient.DidNotReceive().GetHistoryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // The conversation should have the loaded message
        var agent = _store.GetAgent("agent-1");
        var cronConvId = $"cron-session:{cronSessionId}";
        Assert.True(agent!.Conversations.ContainsKey(cronConvId));
        var conv = agent.Conversations[cronConvId];
        Assert.True(conv.HistoryLoaded);
        Assert.Single(conv.Messages);
        Assert.Equal("Cron executed successfully", conv.Messages[0].Content);
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
}
