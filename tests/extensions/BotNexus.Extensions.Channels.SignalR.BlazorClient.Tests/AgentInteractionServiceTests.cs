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
}
