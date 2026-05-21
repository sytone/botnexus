using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

using NSubstitute;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for canvas-per-conversation routing (#413 Phase 3).
/// </summary>
public sealed class CanvasPerConversationTests
{
    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly GatewayHubConnection _hub = new();
    private readonly GatewayEventHandler _handler;
    private readonly AgentInteractionService _interaction;

    public CanvasPerConversationTests()
    {
        _handler = new GatewayEventHandler(_store, _hub);
        _interaction = new AgentInteractionService(_store, _hub, _restClient);
    }

    private AgentState SeedAgent(string agentId)
    {
        _store.SeedAgents([new AgentSummary(agentId, "Test Agent")]);
        return _store.GetAgent(agentId)!;
    }

    private ConversationState SeedConversation(AgentState agent, string convId)
    {
        _store.SeedConversations(agent.AgentId, [
            new ConversationSummaryDto(convId, agent.AgentId, "Test", false, "Active", null, 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        return agent.Conversations[convId];
    }

    // ── HandleCanvasUpdated routes to conversation ────────────────────────

    [Fact]
    public void HandleCanvasUpdated_RoutesToConversation_NotAgent()
    {
        var agent = SeedAgent("agent-1");
        var conv = SeedConversation(agent, "conv-1");

        _handler.HandleCanvasUpdated("agent-1", "conv-1", "<h1>Hello</h1>");

        Assert.Equal("<h1>Hello</h1>", conv.CanvasHtml);
        Assert.NotNull(conv.CanvasUpdatedAt);
        // Agent-level canvas should NOT be set
        Assert.Null(agent.CanvasHtml);
    }

    [Fact]
    public void HandleCanvasUpdated_EmptyHtml_ClearsConversationCanvas()
    {
        var agent = SeedAgent("agent-2");
        var conv = SeedConversation(agent, "conv-2");
        conv.CanvasHtml = "<p>Old</p>";

        _handler.HandleCanvasUpdated("agent-2", "conv-2", "");

        Assert.Null(conv.CanvasHtml);
    }

    [Fact]
    public void HandleCanvasUpdated_UnknownConversation_DoesNotThrow()
    {
        var agent = SeedAgent("agent-3");

        // Should not throw even when conversationId doesn't exist in the store
        _handler.HandleCanvasUpdated("agent-3", "unknown-conv", "<p>test</p>");

        Assert.Null(agent.CanvasHtml);
    }

    [Fact]
    public void HandleCanvasUpdated_UnknownAgent_DoesNotThrow()
    {
        // Should not throw when agent doesn't exist
        _handler.HandleCanvasUpdated("unknown-agent", "conv-1", "<p>test</p>");
    }

    [Fact]
    public void HandleCanvasUpdated_SwitchConversations_EachHasOwnCanvas()
    {
        var agent = SeedAgent("agent-4");
        var convA = SeedConversation(agent, "conv-a");
        var convB = SeedConversation(agent, "conv-b");

        _handler.HandleCanvasUpdated("agent-4", "conv-a", "<h1>Conv A</h1>");
        _handler.HandleCanvasUpdated("agent-4", "conv-b", "<h2>Conv B</h2>");

        Assert.Equal("<h1>Conv A</h1>", convA.CanvasHtml);
        Assert.Equal("<h2>Conv B</h2>", convB.CanvasHtml);
    }

    // ── SelectConversation fetches canvas from REST ───────────────────────

    [Fact]
    public async Task SelectConversationAsync_FetchesCanvasFromRest_WhenCanvasIsNull()
    {
        var agent = SeedAgent("agent-5");
        var conv = SeedConversation(agent, "conv-5");

        _restClient.GetConversationCanvasAsync("agent-5", "conv-5", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("<p>Persisted</p>"));

        await _interaction.SelectConversationAsync("agent-5", "conv-5");

        Assert.Equal("<p>Persisted</p>", conv.CanvasHtml);
    }

    [Fact]
    public async Task SelectConversationAsync_DoesNotOverwriteExistingCanvas()
    {
        var agent = SeedAgent("agent-6");
        var conv = SeedConversation(agent, "conv-6");
        conv.CanvasHtml = "<p>Already Live</p>";

        await _interaction.SelectConversationAsync("agent-6", "conv-6");

        // REST should not be called when canvas is already populated
        await _restClient.DidNotReceive().GetConversationCanvasAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal("<p>Already Live</p>", conv.CanvasHtml);
    }
}
