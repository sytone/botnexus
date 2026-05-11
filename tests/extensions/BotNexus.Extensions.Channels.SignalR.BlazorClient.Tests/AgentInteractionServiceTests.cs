using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AgentInteractionServiceTests
{
    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly AgentInteractionService _service;

    public AgentInteractionServiceTests()
    {
        _service = new AgentInteractionService(_store, new GatewayHubConnection(), _restClient);
        _store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            IsConnected = true
        });
    }

    [Fact]
    public async Task CreateConversationAsync_adds_conversation_and_selects_it()
    {
        _restClient.CreateConversationAsync(Arg.Any<CreateConversationRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationResponseDto(
                "conv-1",
                "agent-1",
                "New conversation",
                true,
                "Active",
                null,
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));

        var conversationId = await _service.CreateConversationAsync("agent-1", select: true);

        var agent = _store.GetAgent("agent-1")!;
        Assert.Equal("conv-1", conversationId);
        Assert.Equal("conv-1", agent.ActiveConversationId);
        Assert.True(agent.Conversations.ContainsKey("conv-1"));
    }

    [Fact]
    public async Task SelectConversationAsync_sets_active_conversation()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "One",
            HistoryLoaded = true
        };
        agent.Conversations["conv-2"] = new ConversationState
        {
            ConversationId = "conv-2",
            Title = "Two",
            HistoryLoaded = true
        };

        await _service.SelectConversationAsync("agent-1", "conv-2");

        Assert.Equal("conv-2", agent.ActiveConversationId);
    }

    [Fact]
    public void ClearLocalMessages_clears_active_conversation_and_adds_system_message()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.ActiveConversationId = "conv-1";
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "One",
            HistoryLoaded = true
        };
        agent.Conversations["conv-1"].Messages.Add(new ChatMessage("User", "hello", DateTimeOffset.UtcNow));

        _service.ClearLocalMessages("agent-1");

        var messages = agent.Conversations["conv-1"].Messages;
        Assert.Single(messages);
        Assert.Equal("System", messages[0].Role);
        Assert.False(agent.Conversations["conv-1"].HistoryLoaded);
    }

    // ── Conversation history loading tests ────────────────────────────────

    [Fact]
    public async Task SelectConversation_uses_first_page_when_totalCount_exceeds_limit()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Long conv",
            HistoryLoaded = false
        };

        // API returns the newest entries on offset=0, even for long conversations.
        var firstPage = new ConversationHistoryResponseDto(
            "conv-1", TotalCount: 272, Offset: 0, Limit: 200,
            Entries: Enumerable.Range(72, 200).Select(i => new ConversationHistoryEntryDto
            {
                Kind = "message",
                SessionId = "s1",
                Role = i >= 270 ? "user" : "assistant",
                Content = $"msg-{i}",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-300 + i)
            }).ToList());

        _restClient.GetHistoryAsync("conv-1", 200, 0, Arg.Any<CancellationToken>())
            .Returns(firstPage);

        await _service.SelectConversationAsync("agent-1", "conv-1");

        // Should only fetch once; a second call pages away from newest data.
        await _restClient.Received(1).GetHistoryAsync("conv-1", 200, 0, Arg.Any<CancellationToken>());
        await _restClient.DidNotReceive().GetHistoryAsync("conv-1", 200, 72, Arg.Any<CancellationToken>());

        // Messages come directly from the first page (latest entries).
        var messages = agent.Conversations["conv-1"].Messages;
        Assert.Equal(200, messages.Count);
        Assert.Equal("msg-72", messages[0].Content);
        Assert.Equal("msg-271", messages[^1].Content);
    }

    [Fact]
    public async Task SelectConversation_uses_single_fetch_when_totalCount_within_limit()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Short conv",
            HistoryLoaded = false
        };

        var response = new ConversationHistoryResponseDto(
            "conv-1", TotalCount: 5, Offset: 0, Limit: 200,
            Entries: Enumerable.Range(0, 5).Select(i => new ConversationHistoryEntryDto
            {
                Kind = "message",
                SessionId = "s1",
                Role = "user",
                Content = $"msg-{i}",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i)
            }).ToList());

        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(response);

        await _service.SelectConversationAsync("agent-1", "conv-1");

        // Only one REST call needed
        await _restClient.Received(1).GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        var messages = agent.Conversations["conv-1"].Messages;
        Assert.Equal(5, messages.Count);
    }

    [Fact]
    public async Task RefreshConversationsAsync_AddsVirtualCronConversationRows()
    {
        var agent = _store.GetAgent("agent-1")!;
        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns([
                new ConversationSummaryDto(
                    "conv-1",
                    "agent-1",
                    "General",
                    false,
                    "Active",
                    null,
                    0,
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddMinutes(-5))
            ]);
        _restClient.GetSessionsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns([
                new SessionSummary(
                    "cron:job-1:20260508010101:abc123",
                    "agent-1",
                    "cron",
                    "cron",
                    "Active",
                    2,
                    false,
                    DateTimeOffset.UtcNow.AddMinutes(-10),
                    DateTimeOffset.UtcNow.AddMinutes(-1))
            ]);

        await _service.RefreshConversationsAsync("agent-1");

        var cronConversation = agent.Conversations["cron-session:cron:job-1:20260508010101:abc123"];
        Assert.True(cronConversation.IsVirtualSession);
        Assert.Equal("cron", cronConversation.VirtualSessionKind);
        Assert.Equal("cron:job-1:20260508010101:abc123", cronConversation.ActiveSessionId);
    }

    [Fact]
    public async Task SelectConversation_ForVirtualCronConversation_LoadsSessionHistory()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["cron-session:cron:job-1:run"] = new ConversationState
        {
            ConversationId = "cron-session:cron:job-1:run",
            Title = "Cron session",
            IsVirtualSession = true,
            VirtualSessionKind = "cron",
            ActiveSessionId = "cron:job-1:run",
            HistoryLoaded = false
        };

        _restClient.GetSessionHistoryAsync("cron:job-1:run", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SessionHistoryResponseDto(
                0,
                200,
                1,
                [
                    new SessionHistoryEntryDto
                    {
                        Role = "assistant",
                        Content = "Cron execution complete",
                        Timestamp = DateTimeOffset.UtcNow
                    }
                ]));

        await _service.SelectConversationAsync("agent-1", "cron-session:cron:job-1:run");

        await _restClient.Received(1).GetSessionHistoryAsync("cron:job-1:run", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        var messages = agent.Conversations["cron-session:cron:job-1:run"].Messages;
        Assert.Single(messages);
        Assert.Equal("Cron execution complete", messages[0].Content);
    }

    [Fact]
    public async Task ArchiveConversationAsync_ForVirtualCronConversation_DeletesSessionAndRemovesConversation()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "General",
            IsDefault = false,
            HistoryLoaded = true
        };
        agent.Conversations["cron-session:cron:job-1:run"] = new ConversationState
        {
            ConversationId = "cron-session:cron:job-1:run",
            Title = "Cron session",
            IsDefault = false,
            IsVirtualSession = true,
            VirtualSessionKind = "cron",
            ActiveSessionId = "cron:job-1:run",
            HistoryLoaded = true
        };
        _store.SetActiveConversation("agent-1", "cron-session:cron:job-1:run");

        _restClient.DeleteSessionAsync("cron:job-1:run", Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.ArchiveConversationAsync("agent-1", "cron-session:cron:job-1:run");

        // Should route through session deletion, not conversation archive
        await _restClient.Received(1).DeleteSessionAsync("cron:job-1:run", Arg.Any<CancellationToken>());
        await _restClient.DidNotReceive().ArchiveConversationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        agent.Conversations.ContainsKey("cron-session:cron:job-1:run").ShouldBeFalse();
        agent.ActiveConversationId.ShouldBe("conv-1");
    }

    [Fact]
    public async Task ArchiveConversationAsync_ForVirtualCronWithColonsInId_DeletesCorrectSession()
    {
        var agent = _store.GetAgent("agent-1")!;
        const string sessionId = "cron:20260509002033:6f2f84a4f1634ff492a4fec212872c54";
        var cronKey = $"cron-session:{sessionId}";

        agent.Conversations[cronKey] = new ConversationState
        {
            ConversationId = cronKey,
            Title = "Cron · cron:202",
            IsVirtualSession = true,
            VirtualSessionKind = "cron",
            ActiveSessionId = sessionId,
            HistoryLoaded = true
        };
        _store.SetActiveConversation("agent-1", cronKey);

        _restClient.DeleteSessionAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.ArchiveConversationAsync("agent-1", cronKey);

        await _restClient.Received(1).DeleteSessionAsync(sessionId, Arg.Any<CancellationToken>());
        await _restClient.DidNotReceive().ArchiveConversationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        agent.Conversations.ContainsKey(cronKey).ShouldBeFalse();
    }

    [Fact]
    public async Task ArchiveConversationAsync_ForStaleOrphanCronWithNoSession_RemovesLocallyWithoutApiCall()
    {
        var agent = _store.GetAgent("agent-1")!;
        const string cronKey = "cron-session:cron:stale:orphan";
        agent.Conversations[cronKey] = new ConversationState
        {
            ConversationId = cronKey,
            Title = "Cron · stale",
            IsVirtualSession = true,
            VirtualSessionKind = "cron",
            ActiveSessionId = null, // stale — no backing session
            HistoryLoaded = false
        };

        await _service.ArchiveConversationAsync("agent-1", cronKey);

        // No REST calls should be made for stale orphans
        await _restClient.DidNotReceive().ArchiveConversationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _restClient.DidNotReceive().DeleteSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        agent.Conversations.ContainsKey(cronKey).ShouldBeFalse();
    }

    [Theory]
    [InlineData("cron:20260509002033:6f2f84a4f1634ff492a4fec212872c54")]
    [InlineData("cron:20260510001608:c7fe67628e3142a1894974d22bb998a8")]
    public async Task ArchiveConversationAsync_ForLegacyCronProjection_WhenArchiveFails_FallsBackToSessionCleanup(string sessionId)
    {
        var agent = _store.GetAgent("agent-1")!;
        var cronKey = $"cron-session:{sessionId}";
        agent.Conversations[cronKey] = new ConversationState
        {
            ConversationId = cronKey,
            Title = "Legacy cron projection",
            IsDefault = false,
            // Legacy projection: no virtual flags and no active session linkage in local state.
            ActiveSessionId = null,
            HistoryLoaded = false
        };

        _restClient.ArchiveConversationAsync(cronKey, Arg.Any<CancellationToken>())
            .Returns(false);
        _restClient.DeleteSessionAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.ArchiveConversationAsync("agent-1", cronKey);

        await _restClient.Received(1).ArchiveConversationAsync(cronKey, Arg.Any<CancellationToken>());
        await _restClient.Received(1).DeleteSessionAsync(sessionId, Arg.Any<CancellationToken>());
        agent.Conversations.ContainsKey(cronKey).ShouldBeFalse();
    }

    [Fact]
    public async Task ArchiveConversationAsync_ForNormalConversation_StillUsesConversationArchive()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-normal"] = new ConversationState
        {
            ConversationId = "conv-normal",
            Title = "Normal conversation",
            IsDefault = false,
            HistoryLoaded = true
        };
        _store.SetActiveConversation("agent-1", "conv-normal");

        _restClient.ArchiveConversationAsync("conv-normal", Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.ArchiveConversationAsync("agent-1", "conv-normal");

        await _restClient.Received(1).ArchiveConversationAsync("conv-normal", Arg.Any<CancellationToken>());
        await _restClient.DidNotReceive().DeleteSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        agent.Conversations.ContainsKey("conv-normal").ShouldBeFalse();
    }

    // ── Steering tests ────────────────────────────────────────────────────

    [Fact]
    public async Task SteerAsync_skips_when_agent_has_no_session()
    {
        var agent = _store.GetAgent("agent-1")!;
        // No SessionId, no ActiveConversationSessionId
        Assert.Null(agent.ActiveConversationSessionId);

        await _service.SteerAsync("agent-1", "redirect me");

        // No local message should be appended because SteerAsync bails early
        Assert.Null(agent.ActiveConversationId);
    }

    [Fact]
    public async Task SteerAsync_appends_steering_prefix_to_local_messages()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.SessionId = "sess-1";
        agent.ActiveConversationId = "conv-1";
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Test conv",
            ActiveSessionId = "sess-1"
        };

        // SteerAsync will fail on the hub call (no connection) and append an error,
        // but the user steering message should be appended first.
        await _service.SteerAsync("agent-1", "redirect me");

        var conv = agent.Conversations["conv-1"];
        Assert.True(conv.Messages.Count >= 1);
        Assert.Equal("User", conv.Messages[0].Role);
        Assert.Equal("🔀 redirect me", conv.Messages[0].Content);
    }

    [Fact]
    public async Task SteerAsync_appends_error_on_hub_failure()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.SessionId = "sess-1";
        agent.ActiveConversationId = "conv-1";
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Test conv",
            ActiveSessionId = "sess-1"
        };

        await _service.SteerAsync("agent-1", "redirect me");

        var conv = agent.Conversations["conv-1"];
        // First message is the user steering, second is the error
        Assert.Equal(2, conv.Messages.Count);
        Assert.Equal("Error", conv.Messages[1].Role);
        Assert.Contains("Steer failed", conv.Messages[1].Content);
    }
}
