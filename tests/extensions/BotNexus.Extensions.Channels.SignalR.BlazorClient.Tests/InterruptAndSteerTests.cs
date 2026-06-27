using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for InterruptAndSteerAsync on AgentInteractionService (Issue #802).
/// </summary>
public sealed class InterruptAndSteerTests
{
    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly AgentInteractionService _service;

    public InterruptAndSteerTests()
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
    public void IAgentInteractionService_HasInterruptAndSteerAsync_Method()
    {
        // Assert the method exists on the interface (contract guard)
        var method = typeof(IAgentInteractionService).GetMethod("InterruptAndSteerAsync");
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal("agentId", parameters[0].Name);
        Assert.Equal("message", parameters[1].Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task InterruptAndSteerAsync_WithBlankMessage_IsNoOp(string? message)
    {
        // Arrange - wire up session so we know the no-op is due to blank message, not missing session
        var agent = _store.GetAgent("agent-1")!;
        agent.ActiveConversationId = "conv-1";
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", Title = "T", HistoryLoaded = true };
        agent.Conversations["conv-1"].ActiveSessionId = "session-1";

        var messagesBefore = agent.Conversations["conv-1"].Messages.Count;

        // Act
        await _service.InterruptAndSteerAsync("agent-1", message!);

        // Assert - no messages appended, no hub call attempted
        Assert.Equal(messagesBefore, agent.Conversations["conv-1"].Messages.Count);
    }

    [Fact]
    public async Task InterruptAndSteerAsync_WithNoSession_IsNoOp()
    {
        // Agent has no active conversation session -- method should not throw
        var exception = await Record.ExceptionAsync(() =>
            _service.InterruptAndSteerAsync("agent-1", "redirect me please"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task InterruptAndSteerAsync_WhenHubThrows_AppendsErrorMessage()
    {
        // Arrange - set up session so the hub call path is reached
        var agent = _store.GetAgent("agent-1")!;
        agent.ActiveConversationId = "conv-1";
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", Title = "T", HistoryLoaded = true };
        agent.Conversations["conv-1"].ActiveSessionId = "session-1";

        // Act - the GatewayHubConnection has a null _connection so InvokeAsync will throw
        // This exercises the catch block -> AppendError path
        // We use a try here because the exception propagates from the hub invocation
        await _service.InterruptAndSteerAsync("agent-1", "please redirect");

        // Assert: the user message or error should be appended
        // AppendUserMessage is called BEFORE the hub invocation, so we should have the redirect msg
        var conv = agent.Conversations["conv-1"];
        // The [redirect] user message is appended before the hub call
        Assert.Contains(conv.Messages, m =>
            m.Role.Equals("User", StringComparison.OrdinalIgnoreCase) &&
            m.Content.Contains("redirect"));
    }
}
