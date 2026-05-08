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

    // ── History tail-fetch tests ──────────────────────────────────────────

    [Fact]
    public async Task SelectConversation_loads_history_from_tail_when_totalCount_exceeds_limit()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Long conv",
            HistoryLoaded = false
        };

        // First call returns oldest 200 of 272 total
        var firstPage = new ConversationHistoryResponseDto(
            "conv-1", TotalCount: 272, Offset: 0, Limit: 200,
            Entries: Enumerable.Range(0, 200).Select(i => new ConversationHistoryEntryDto
            {
                Kind = "message",
                SessionId = "s1",
                Role = "user",
                Content = $"old-{i}",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-300 + i)
            }).ToList());

        // Second call returns latest 200 from offset 72
        var tailPage = new ConversationHistoryResponseDto(
            "conv-1", TotalCount: 272, Offset: 72, Limit: 200,
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
        _restClient.GetHistoryAsync("conv-1", 200, 72, Arg.Any<CancellationToken>())
            .Returns(tailPage);

        await _service.SelectConversationAsync("agent-1", "conv-1");

        // Should have made two REST calls: first page + tail page
        await _restClient.Received(1).GetHistoryAsync("conv-1", 200, 0, Arg.Any<CancellationToken>());
        await _restClient.Received(1).GetHistoryAsync("conv-1", 200, 72, Arg.Any<CancellationToken>());

        // Messages should come from the tail page (most recent)
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
}
