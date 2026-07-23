using System.Net;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AgentInteractionServiceTests
{
    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly AgentInteractionService _service;

    public AgentInteractionServiceTests()
    {
        _service = new AgentInteractionService(_store, new GatewayHubConnection(), _restClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentInteractionService>.Instance);
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
        agent.Conversations["conv-1"].AppendMessage(new ChatMessage("User", "hello", DateTimeOffset.UtcNow));

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

        // #1691: the API returns the newest 20 entries on offset=0; the client opens on that page
        // and pages backwards on scroll-up rather than pulling the whole transcript.
        var firstPage = new ConversationHistoryResponseDto(
            "conv-1", TotalCount: 272, Offset: 0, Limit: 20,
            Entries: Enumerable.Range(252, 20).Select(i => new ConversationHistoryEntryDto
            {
                Kind = "message",
                SessionId = "s1",
                Role = i >= 270 ? "user" : "assistant",
                Content = $"msg-{i}",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-300 + i)
            }).ToList());

        _restClient.GetHistoryAsync("conv-1", 20, 0, Arg.Any<CancellationToken>())
            .Returns(firstPage);

        await _service.SelectConversationAsync("agent-1", "conv-1");

        // Should fetch the most-recent 20-row page once; older pages come later via LoadMoreHistoryAsync.
        await _restClient.Received(1).GetHistoryAsync("conv-1", 20, 0, Arg.Any<CancellationToken>());
        await _restClient.DidNotReceive().GetHistoryAsync("conv-1", 20, 20, Arg.Any<CancellationToken>());

        // Messages come directly from the first page (latest entries) and more remain to be paged.
        var conv = agent.Conversations["conv-1"];
        Assert.Equal(20, conv.Messages.Count);
        Assert.Equal("msg-252", conv.Messages[0].Content);
        Assert.Equal("msg-271", conv.Messages[^1].Content);
        Assert.True(conv.HasMoreHistory);
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
            "conv-1", TotalCount: 5, Offset: 0, Limit: 20,
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
    public async Task SelectConversation_when_history_fetch_throws_marks_HistoryLoadFailed()
    {
        // #1697: a non-404 history fetch failure must flag the conversation so the message view can
        // show a load-error empty state rather than a silent blank pane (and never spin forever).
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Boom",
            HistoryLoaded = false
        };

        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ConversationHistoryResponseDto?>(_ => throw new HttpRequestException("500"));

        await _service.SelectConversationAsync("agent-1", "conv-1");

        var conv = agent.Conversations["conv-1"];
        conv.HistoryLoadFailed.ShouldBeTrue();
        conv.IsLoadingHistory.ShouldBeFalse();
        conv.HistoryLoaded.ShouldBeTrue(); // marked loaded so the view stops showing the spinner
    }

    [Fact]
    public async Task SelectConversation_when_history_loads_empty_clears_HistoryLoadFailed()
    {
        // #1697: a successful load (even an empty one) leaves HistoryLoadFailed false so the view shows
        // the neutral "No messages yet" state, not the error state.
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Empty",
            HistoryLoaded = false,
            HistoryLoadFailed = true // pretend a prior attempt failed
        };

        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationHistoryResponseDto("conv-1", TotalCount: 0, Offset: 0, Limit: 200, Entries: []));

        await _service.SelectConversationAsync("agent-1", "conv-1");

        var conv = agent.Conversations["conv-1"];
        conv.HistoryLoadFailed.ShouldBeFalse();
        conv.Messages.Count.ShouldBe(0);
    }

    [Fact]
    public async Task RefreshConversationsAsync_RepeatedCronRunsReuseSingleConversationKey()
    {
        var agent = _store.GetAgent("agent-1")!;
        const string stableConversationId = "conv-cron-job-1";
        const string firstRunSessionId = "cron:job-1:20260508010101:abc123";
        const string secondRunSessionId = "cron:job-1:20260508020101:def456";

        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns([
                new ConversationSummaryDto(
                    stableConversationId,
                    "agent-1",
                    "cron:job-1",
                    false,
                    "Active",
                    secondRunSessionId,
                    0,
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddMinutes(-1))
            ]);
        _restClient.GetSessionsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns([
                new SessionSummary(
                    firstRunSessionId,
                    "agent-1",
                    "cron",
                    "cron",
                    "Completed",
                    12,
                    false,
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddMinutes(-10)),
                new SessionSummary(
                    secondRunSessionId,
                    "agent-1",
                    "cron",
                    "cron",
                    "Active",
                    2,
                    false,
                    DateTimeOffset.UtcNow.AddMinutes(-3),
                    DateTimeOffset.UtcNow.AddMinutes(-1))
            ]);

        await _service.RefreshConversationsAsync("agent-1");

        Assert.True(agent.Conversations.ContainsKey(stableConversationId));
        Assert.DoesNotContain(agent.Conversations.Keys, key => key.StartsWith("cron-session:", StringComparison.Ordinal));
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
    public async Task ArchiveConversationAsync_ForVirtualCronConversation_ArchivesConversationNotSession()
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

        _restClient.ArchiveConversationAsync("cron-session:cron:job-1:run", Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.ArchiveConversationAsync("agent-1", "cron-session:cron:job-1:run");

        // Must route through conversation archive (not session deletion) to preserve session records
        await _restClient.Received(1).ArchiveConversationAsync("cron-session:cron:job-1:run", Arg.Any<CancellationToken>());
        agent.Conversations.ContainsKey("cron-session:cron:job-1:run").ShouldBeFalse();
        agent.ActiveConversationId.ShouldBe("conv-1");
    }

    [Fact]
    public async Task ViewSubAgentAsync_loads_session_history_into_read_only_virtual_conversation()
    {
        _restClient.GetSessionHistoryAsync("sub-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SessionHistoryResponseDto(
                0,
                50,
                1,
                [
                    new SessionHistoryEntryDto
                    {
                        Role = "assistant",
                        Content = "sub-agent result",
                        Timestamp = DateTimeOffset.UtcNow
                    }
                ]));

        await _service.ViewSubAgentAsync(new SubAgentInfo
        {
            SubAgentId = "sub-1",
            Name = "Scout",
            Task = "Inspect repository"
        });

        await _restClient.Received(1).GetSessionHistoryAsync("sub-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _restClient.DidNotReceive().GetHistoryAsync("sub-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        var subAgent = _store.GetAgent("sub-1");
        Assert.NotNull(subAgent);
        Assert.Equal("agent-subagent", subAgent.SessionType);
        Assert.Equal("subagent-session:sub-1", subAgent.ActiveConversationId);

        var conversation = subAgent.Conversations["subagent-session:sub-1"];
        Assert.True(conversation.IsVirtualSession);
        Assert.Equal("subagent", conversation.VirtualSessionKind);
        Assert.Single(conversation.Messages);
        Assert.Equal("sub-agent result", conversation.Messages[0].Content);
    }

    [Fact]
    public async Task ArchiveConversationAsync_ForVirtualCronWithColonsInId_ArchivesWithEncodedConversationId()
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

        _restClient.ArchiveConversationAsync(cronKey, Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.ArchiveConversationAsync("agent-1", cronKey);

        // Calls conversations endpoint with full cron-session:... ID, not sessions endpoint
        await _restClient.Received(1).ArchiveConversationAsync(cronKey, Arg.Any<CancellationToken>());
        agent.Conversations.ContainsKey(cronKey).ShouldBeFalse();
    }

    [Fact]
    public async Task ArchiveConversationAsync_ForStaleOrphanCron_CallsConversationArchive()
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

        // Backend returns 204 for cron-session: IDs with missing backing sessions
        _restClient.ArchiveConversationAsync(cronKey, Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.ArchiveConversationAsync("agent-1", cronKey);

        // Stale orphans still call the conversations endpoint (backend handles idempotently)
        await _restClient.Received(1).ArchiveConversationAsync(cronKey, Arg.Any<CancellationToken>());
        agent.Conversations.ContainsKey(cronKey).ShouldBeFalse();
    }

    [Theory]
    [InlineData("cron:20260509002033:6f2f84a4f1634ff492a4fec212872c54")]
    [InlineData("cron:20260510001608:c7fe67628e3142a1894974d22bb998a8")]
    public async Task ArchiveConversationAsync_ForLegacyCronProjection_UsesConversationArchive(string sessionId)
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
            .Returns(true);

        await _service.ArchiveConversationAsync("agent-1", cronKey);

        // Legacy projections now route through conversation archive (backend returns 204 for virtual cron IDs)
        await _restClient.Received(1).ArchiveConversationAsync(cronKey, Arg.Any<CancellationToken>());
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
        agent.Conversations.ContainsKey("conv-normal").ShouldBeFalse();
    }

    // ── Pin tests ───────────────────────────────────────

    [Fact]
    public async Task SetConversationPinnedAsync_pins_and_updates_local_state()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Pin me",
            IsPinned = false
        };
        _restClient.PinConversationAsync("conv-1", true, Arg.Any<CancellationToken>()).Returns(true);

        await _service.SetConversationPinnedAsync("agent-1", "conv-1", pinned: true);

        await _restClient.Received(1).PinConversationAsync("conv-1", true, Arg.Any<CancellationToken>());
        agent.Conversations["conv-1"].IsPinned.ShouldBeTrue();
    }

    [Fact]
    public async Task SetConversationPinnedAsync_unpins_and_updates_local_state()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Unpin me",
            IsPinned = true
        };
        _restClient.PinConversationAsync("conv-1", false, Arg.Any<CancellationToken>()).Returns(true);

        await _service.SetConversationPinnedAsync("agent-1", "conv-1", pinned: false);

        await _restClient.Received(1).PinConversationAsync("conv-1", false, Arg.Any<CancellationToken>());
        agent.Conversations["conv-1"].IsPinned.ShouldBeFalse();
    }

    [Fact]
    public async Task SetConversationPinnedAsync_rolls_back_when_gateway_fails()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Pin me",
            IsPinned = false
        };
        _restClient.PinConversationAsync("conv-1", true, Arg.Any<CancellationToken>()).Returns(false);

        await _service.SetConversationPinnedAsync("agent-1", "conv-1", pinned: true);

        // Optimistic flip is reverted when the persist call reports failure.
        agent.Conversations["conv-1"].IsPinned.ShouldBeFalse();
    }

    [Fact]
    public async Task SetConversationPinnedAsync_noops_when_already_in_desired_state()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Already pinned",
            IsPinned = true
        };

        await _service.SetConversationPinnedAsync("agent-1", "conv-1", pinned: true);

        await _restClient.DidNotReceive().PinConversationAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
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
    // -- SelectConversationAsync stale streaming state tests (#789) --

    [Fact]
    public async Task SelectConversationAsync_StaleStreamingConversation_ClearsStreamingAndReloadsHistory()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-streaming"] = new ConversationState
        {
            ConversationId = "conv-streaming",
            Title = "Stale streamer",
            HistoryLoaded = true
        };
        agent.Conversations["conv-streaming"].StreamState.IsStreaming = true;

        var freshHistory = new ConversationHistoryResponseDto(
            "conv-streaming", TotalCount: 2, Offset: 0, Limit: 200,
            Entries:
            [
                new ConversationHistoryEntryDto { Kind = "message", SessionId = "s1", Role = "user",      Content = "hello",     Timestamp = DateTimeOffset.UtcNow.AddSeconds(-5) },
                new ConversationHistoryEntryDto { Kind = "message", SessionId = "s1", Role = "assistant", Content = "completed", Timestamp = DateTimeOffset.UtcNow }
            ]);

        _restClient.GetHistoryAsync("conv-streaming", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(freshHistory);

        await _service.SelectConversationAsync("agent-1", "conv-streaming");

        await _restClient.Received(1).GetHistoryAsync("conv-streaming", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        var convResult = agent.Conversations["conv-streaming"];
        Assert.False(convResult.StreamState.IsStreaming);
        Assert.Equal(2, convResult.Messages.Count);
        Assert.Equal("completed", convResult.Messages[^1].Content);
    }

    [Fact]
    public async Task SelectConversationAsync_AlreadyLoadedNonStreaming_DoesNotReloadHistory()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-idle"] = new ConversationState
        {
            ConversationId = "conv-idle",
            Title = "Idle",
            HistoryLoaded = true
        };

        await _service.SelectConversationAsync("agent-1", "conv-idle");

        await _restClient.DidNotReceive().GetHistoryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectConversationAsync_StreamingAndNotLoaded_LoadsHistoryAndClearsStreaming()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-new-streaming"] = new ConversationState
        {
            ConversationId = "conv-new-streaming",
            Title = "New streaming",
            HistoryLoaded = false
        };
        agent.Conversations["conv-new-streaming"].StreamState.IsStreaming = true;

        var historyResponse = new ConversationHistoryResponseDto(
            "conv-new-streaming", TotalCount: 1, Offset: 0, Limit: 200,
            Entries:
            [
                new ConversationHistoryEntryDto { Kind = "message", SessionId = "s1", Role = "user", Content = "go", Timestamp = DateTimeOffset.UtcNow }
            ]);

        _restClient.GetHistoryAsync("conv-new-streaming", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(historyResponse);

        await _service.SelectConversationAsync("agent-1", "conv-new-streaming");

        await _restClient.Received(1).GetHistoryAsync("conv-new-streaming", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        var conv = agent.Conversations["conv-new-streaming"];
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Single(conv.Messages);
    }


    // ---- Stream/history reconciliation tests (issue #759) ----


    [Fact]
    public async Task LoadHistory_not_called_when_streaming_active()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.ActiveConversationId = "conv-active";
        agent.Conversations["conv-active"] = new ConversationState
        {
            ConversationId = "conv-active",
            Title = "Active",
            HistoryLoaded = false,
            ActiveSessionId = "sess-active"
        };
        var conv = agent.Conversations["conv-active"];
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "in progress";
        conv.AppendMessage(new ChatMessage("Tool", "running tool", DateTimeOffset.UtcNow));

        // Trigger SelectConversation which internally calls LoadConversationHistoryAsync
        // But fix #789 resets IsStreaming first, so we need to test the direct path.
        // Instead, use the internal mechanism: set HistoryLoaded=false then call Select.
        // The SelectConversation fix (#789) resets streaming first, allowing reload.
        // Our guard protects the non-SelectConversation path (DrainPending, RefreshConversations).
        // To test our guard: manually trigger via the public RefreshConversationsAsync.

        _restClient.GetConversationsAsync(Arg.Any<string>())
            .Returns(new List<ConversationSummaryDto>
            {
                new("conv-active", "agent-1", "Active", false, "Active", "sess-active", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            });
        _restClient.GetSessionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<SessionSummary>() as IReadOnlyList<SessionSummary>);

        // This should NOT trigger a history load because conversation is streaming
        await _service.RefreshConversationsAsync("agent-1");

        // History endpoint should NOT have been called
        await _restClient.DidNotReceive().GetHistoryAsync(Arg.Any<string>(), Arg.Any<int>());

        // Messages should be preserved
        Assert.Single(conv.Messages);
        Assert.Equal("running tool", conv.Messages[0].Content);
    }

    // -- ToChatMessage projection factory tests (#1623) --
    // These lock the single-source projection contract shared by the virtual, regular,
    // and sub-agent history loaders: tool-call detection, ANSI-stripped tool result,
    // and role mapping.

    [Fact]
    public void ToChatMessage_ToolCallEntry_MapsRole_FlagsToolCall_AndStripsAnsiResult()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var entry = new SessionHistoryEntryDto
        {
            Role = "tool",
            Content = "\u001b[31mred\u001b[0m output",
            Timestamp = timestamp,
            ToolName = "exec",
            ToolCallId = "call-1",
            ToolArgs = "{\"cmd\":\"ls\"}",
            ToolIsError = true,
            ThinkingContent = "deliberating"
        };

        var message = AgentInteractionService.ToChatMessage(entry);

        message.Role.ShouldBe("Tool");
        message.IsToolCall.ShouldBeTrue();
        // ANSI escape sequences are stripped from the surfaced tool result.
        message.ToolResult.ShouldBe("red output");
        message.ToolName.ShouldBe("exec");
        message.ToolCallId.ShouldBe("call-1");
        message.ToolArgs.ShouldBe("{\"cmd\":\"ls\"}");
        message.ToolIsError.ShouldBe(true);
        message.ThinkingContent.ShouldBe("deliberating");
        message.Content.ShouldBe("\u001b[31mred\u001b[0m output");
        message.Timestamp.ShouldBe(timestamp);
    }

    [Fact]
    public void ToChatMessage_NonToolEntry_MapsRole_AndLeavesToolResultNull()
    {
        var entry = new SessionHistoryEntryDto
        {
            Role = "assistant",
            Content = "plain assistant reply",
            Timestamp = DateTimeOffset.UtcNow,
            ToolName = null
        };

        var message = AgentInteractionService.ToChatMessage(entry);

        message.Role.ShouldBe("Assistant");
        message.IsToolCall.ShouldBeFalse();
        message.ToolResult.ShouldBeNull();
        message.Content.ShouldBe("plain assistant reply");
    }

    [Fact]
    public void ToChatMessage_ConversationEntryOverload_AppliesSameProjection()
    {
        var entry = new ConversationHistoryEntryDto
        {
            Kind = "message",
            SessionId = "s1",
            Role = "tool",
            Content = "\u001b[32mok\u001b[0m",
            Timestamp = DateTimeOffset.UtcNow,
            ToolName = "read",
            ToolIsError = false
        };

        var message = AgentInteractionService.ToChatMessage(entry);

        message.Role.ShouldBe("Tool");
        message.IsToolCall.ShouldBeTrue();
        message.ToolResult.ShouldBe("ok");
        message.ToolName.ShouldBe("read");
    }

    // ── Best-effort REST hydration on SelectConversationAsync (#2022) ──────
    // These lock in that collapsing the canvas/todo/ask_user blocks into one
    // HydrateBestEffortAsync helper preserves behaviour: each artifact still
    // hydrates on select, timestamps are set, and a failing REST call is
    // swallowed (silent degrade) rather than surfacing.

    [Fact]
    public async Task SelectConversationAsync_HydratesCanvasTodoAndAskUser()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-h"] = new ConversationState
        {
            ConversationId = "conv-h",
            Title = "Hydrate",
            HistoryLoaded = true
        };

        _restClient.GetConversationCanvasAsync("agent-1", "conv-h").Returns("<div>canvas</div>");
        _restClient.GetConversationTodoAsync("agent-1", "conv-h").Returns("[{\"text\":\"do it\"}]");
        _restClient.GetConversationPendingAskUserAsync("agent-1", "conv-h")
            .Returns("{\"requestId\":\"req-1\",\"prompt\":\"Proceed?\",\"inputType\":\"free_form\"}");

        await _service.SelectConversationAsync("agent-1", "conv-h");

        var conv = agent.Conversations["conv-h"];
        conv.CanvasHtml.ShouldBe("<div>canvas</div>");
        conv.CanvasUpdatedAt.ShouldNotBeNull();
        conv.TodoJson.ShouldBe("[{\"text\":\"do it\"}]");
        conv.TodoUpdatedAt.ShouldNotBeNull();

        var pending = _store.GetPendingAskUser("conv-h");
        pending.ShouldNotBeNull();
        pending!.RequestId.ShouldBe("req-1");
        pending.Prompt.ShouldBe("Proceed?");
    }

    [Fact]
    public async Task SelectConversationAsync_WhenHydrationFetchThrows_SwallowsAndLeavesFieldsUnset()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-fail"] = new ConversationState
        {
            ConversationId = "conv-fail",
            Title = "Fail",
            HistoryLoaded = true
        };

        _restClient.GetConversationCanvasAsync("agent-1", "conv-fail")
            .Returns<string?>(_ => throw new HttpRequestException("boom"));
        _restClient.GetConversationTodoAsync("agent-1", "conv-fail")
            .Returns<string?>(_ => throw new HttpRequestException("boom"));
        _restClient.GetConversationPendingAskUserAsync("agent-1", "conv-fail")
            .Returns<string?>(_ => throw new HttpRequestException("boom"));

        // Must not throw — best-effort hydration silently degrades.
        await _service.SelectConversationAsync("agent-1", "conv-fail");

        var conv = agent.Conversations["conv-fail"];
        conv.CanvasHtml.ShouldBeNull();
        conv.TodoJson.ShouldBeNull();
        _store.GetPendingAskUser("conv-fail").ShouldBeNull();
        agent.ActiveConversationId.ShouldBe("conv-fail");
    }

    [Fact]
    public async Task SelectConversationAsync_WhenHydrationReturnsNull_NoOpsWithoutNotifying()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-null"] = new ConversationState
        {
            ConversationId = "conv-null",
            Title = "Null",
            HistoryLoaded = true
        };

        _restClient.GetConversationCanvasAsync("agent-1", "conv-null").Returns((string?)null);
        _restClient.GetConversationTodoAsync("agent-1", "conv-null").Returns((string?)null);
        _restClient.GetConversationPendingAskUserAsync("agent-1", "conv-null").Returns((string?)null);

        await _service.SelectConversationAsync("agent-1", "conv-null");

        var conv = agent.Conversations["conv-null"];
        conv.CanvasHtml.ShouldBeNull();
        conv.CanvasUpdatedAt.ShouldBeNull();
        conv.TodoJson.ShouldBeNull();
        conv.TodoUpdatedAt.ShouldBeNull();
        _store.GetPendingAskUser("conv-null").ShouldBeNull();
    }

    [Fact]
    public async Task SelectConversationAsync_DoesNotClobberExistingPendingAskUser()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-live"] = new ConversationState
        {
            ConversationId = "conv-live",
            Title = "Live",
            HistoryLoaded = true
        };

        _store.SetPendingAskUser(new AskUserPromptState
        {
            RequestId = "live-req",
            ConversationId = "conv-live",
            Prompt = "Live prompt",
            InputType = "free_form"
        });

        await _service.SelectConversationAsync("agent-1", "conv-live");

        // A live prompt already pending must never be replaced by a slower REST round-trip,
        // and the REST getter must not even be consulted.
        await _restClient.DidNotReceive().GetConversationPendingAskUserAsync("agent-1", "conv-live");
        _store.GetPendingAskUser("conv-live")!.RequestId.ShouldBe("live-req");
    }

    // ---- #2195: Stop force-clears local turn state (escape hatch) ----

    [Fact]
    public async Task AbortAsync_force_clears_local_turn_state_even_when_hub_call_fails()
    {
        // #2195: if a RunEnded is missed/misrouted the conversation is stuck turn-active and the
        // user cannot reply. Stop must always force-clear the local run bracket so the input
        // recovers without a page reload -- even though the (disconnected) hub Abort throws.
        var agent = _store.GetAgent("agent-1")!;
        agent.ActiveConversationId = "conv-1";
        agent.IsStreaming = true;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Test conv",
            ActiveSessionId = "sess-1"
        };
        var conv = agent.Conversations["conv-1"];
        conv.StreamState.IsRunActive = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.ActiveToolCalls["tool-1"] = new ActiveToolCall
        {
            ToolCallId = "tool-1",
            ToolName = "read",
            StartedAt = DateTimeOffset.UtcNow,
            MessageId = "msg-1"
        };
        Assert.True(conv.StreamState.IsTurnActive);

        await _service.AbortAsync("agent-1");

        // The turn-active bracket is cleared locally regardless of the hub result, so the portal
        // swaps back to the normal Send control.
        Assert.False(conv.StreamState.IsRunActive);
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Empty(conv.StreamState.ActiveToolCalls);
        Assert.False(conv.StreamState.IsTurnActive);
        Assert.False(agent.IsStreaming);
    }
}

