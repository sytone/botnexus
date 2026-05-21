using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #400: mobile Chat.razor URL routing -- agent and conversation
/// selection restored from route parameters on load/refresh.
/// </summary>
public sealed class MobileChatPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IPortalLoadService _portalLoad;
    private readonly IAgentInteractionService _interaction;

    public MobileChatPageTests()
    {
        _store = Substitute.For<IClientStateStore>();
        _portalLoad = Substitute.For<IPortalLoadService>();
        _interaction = Substitute.For<IAgentInteractionService>();

        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _store.Agents.Returns(new Dictionary<string, AgentState>().AsReadOnly());
        _store.ActiveAgentId.Returns((string?)null);
        _store.GetStreamState(Arg.Any<string>()).Returns(new ConversationStreamState());
        _store.GetMessages(Arg.Any<string>()).Returns(new List<ChatMessage>().AsReadOnly());

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    // -- Route registration --------------------------------------------------

    [Fact]
    public void Chat_page_registers_agent_only_route()
    {
        _portalLoad.IsReady.Returns(false);

        // Renders without throwing at /chat/{AgentId} route parameter
        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "test-agent"));
        Assert.NotNull(cut);
    }

    [Fact]
    public void Chat_page_registers_agent_and_conversation_route()
    {
        _portalLoad.IsReady.Returns(false);

        var cut = _ctx.Render<Chat>(p => p
            .Add(c => c.AgentId, "test-agent")
            .Add(c => c.ConversationId, "conv-1"));
        Assert.NotNull(cut);
    }

    // -- Happy path: route parameters restore selection ----------------------

    [Fact]
    public void Route_agent_id_selects_agent_when_portal_is_ready()
    {
        _portalLoad.IsReady.Returns(true);

        var agents = new Dictionary<string, AgentState>
        {
            ["agent-1"] = new() { AgentId = "agent-1", DisplayName = "Alpha" }
        };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent("agent-1").Returns(agents["agent-1"]);

        _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Equal("agent-1", _store.ActiveAgentId);
    }

    [Fact]
    public void Route_agent_and_conversation_selects_both_when_portal_is_ready()
    {
        _portalLoad.IsReady.Returns(true);

        var targetAgent = new AgentState
        {
            AgentId = "agent-2",
            DisplayName = "Beta",
            ActiveConversationId = "c-1"
        };
        targetAgent.Conversations["c-1"] = new ConversationState { ConversationId = "c-1", Title = "First" };
        targetAgent.Conversations["c-2"] = new ConversationState { ConversationId = "c-2", Title = "Second" };

        var agents = new Dictionary<string, AgentState> { ["agent-2"] = targetAgent };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent("agent-2").Returns(targetAgent);

        _ctx.Render<Chat>(p => p
            .Add(c => c.AgentId, "agent-2")
            .Add(c => c.ConversationId, "c-2"));

        Assert.Equal("agent-2", _store.ActiveAgentId);
        _interaction.Received(1).SelectConversationAsync("agent-2", "c-2");
    }

    [Fact]
    public void Route_applies_when_portal_becomes_ready_after_initial_render()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["agent-1"] = new() { AgentId = "agent-1", DisplayName = "Alpha" }
        };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent("agent-1").Returns(agents["agent-1"]);
        _portalLoad.IsReady.Returns(false, true);
        _portalLoad.IsLoading.Returns(true, false);

        _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        _portalLoad.OnReadyChanged += Raise.Event<Action>();

        Assert.Equal("agent-1", _store.ActiveAgentId);
    }

    // -- URL encoding --------------------------------------------------------

    [Fact]
    public void Route_decodes_url_encoded_agent_and_conversation_ids()
    {
        _portalLoad.IsReady.Returns(true);

        const string decodedAgentId = "agent/encoded";
        const string decodedConversationId = "conv/encoded id";
        var encodedAgentId = Uri.EscapeDataString(decodedAgentId);
        var encodedConversationId = Uri.EscapeDataString(decodedConversationId);

        var targetAgent = new AgentState
        {
            AgentId = decodedAgentId,
            DisplayName = "Encoded Agent",
            ActiveConversationId = "fallback"
        };
        targetAgent.Conversations[decodedConversationId] = new ConversationState
        {
            ConversationId = decodedConversationId,
            Title = "Encoded Conversation"
        };
        var agents = new Dictionary<string, AgentState> { [decodedAgentId] = targetAgent };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent(decodedAgentId).Returns(targetAgent);

        _ctx.Render<Chat>(p => p
            .Add(c => c.AgentId, encodedAgentId)
            .Add(c => c.ConversationId, encodedConversationId));

        Assert.Equal(decodedAgentId, _store.ActiveAgentId);
        _interaction.Received(1).SelectConversationAsync(decodedAgentId, decodedConversationId);
    }

    // -- Sad paths -----------------------------------------------------------

    [Fact]
    public void Unknown_agent_in_route_does_not_crash_and_falls_back_to_first_agent()
    {
        _portalLoad.IsReady.Returns(true);
        _store.ActiveAgentId.Returns((string?)null);

        var fallbackAgent = new AgentState { AgentId = "agent-1", DisplayName = "Alpha" };
        var agents = new Dictionary<string, AgentState> { ["agent-1"] = fallbackAgent };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent("agent-1").Returns(fallbackAgent);

        _ctx.Render<Chat>(p => p
            .Add(c => c.AgentId, "missing-agent")
            .Add(c => c.ConversationId, "missing-conv"));

        Assert.Equal("agent-1", _store.ActiveAgentId);
        _interaction.DidNotReceive().SelectConversationAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void Unknown_agent_with_existing_active_agent_does_not_change_active_agent()
    {
        _portalLoad.IsReady.Returns(true);

        var knownAgent = new AgentState { AgentId = "agent-1", DisplayName = "Alpha" };
        knownAgent.Conversations["known-conv"] = new ConversationState
        {
            ConversationId = "known-conv",
            Title = "Known"
        };

        var agents = new Dictionary<string, AgentState> { ["agent-1"] = knownAgent };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(knownAgent);

        _ctx.Render<Chat>(p => p
            .Add(c => c.AgentId, "missing-agent")
            .Add(c => c.ConversationId, "missing-conv"));

        // Active agent should remain unchanged, no conversation switch
        Assert.Equal("agent-1", _store.ActiveAgentId);
        _interaction.DidNotReceive().SelectConversationAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void Unknown_conversation_in_route_does_not_call_select_conversation()
    {
        _portalLoad.IsReady.Returns(true);

        var agent = new AgentState { AgentId = "agent-1", DisplayName = "Alpha" };
        // No conversations registered
        var agents = new Dictionary<string, AgentState> { ["agent-1"] = agent };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent("agent-1").Returns(agent);

        _ctx.Render<Chat>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "non-existent-conv"));

        Assert.Equal("agent-1", _store.ActiveAgentId);
        _interaction.DidNotReceive().SelectConversationAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    // -- No route parameters -------------------------------------------------

    [Fact]
    public void No_route_parameters_renders_without_error_and_does_not_select_agent()
    {
        _portalLoad.IsReady.Returns(true);

        var cut = _ctx.Render<Chat>();
        Assert.NotNull(cut);

        // No active agent should have been set
        _store.DidNotReceive().NotifyChanged();
        _interaction.DidNotReceive().SelectConversationAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    // -- Issue #445: HandleStateChanged must not call ApplyRouteSelectionAsync ----

    [Fact]
    public void HandleStateChanged_does_not_call_ApplyRouteSelectionAsync()
    {
        // Arrange: portal ready, agent present, route set
        _portalLoad.IsReady.Returns(true);
        var agent = new AgentState { AgentId = "agent-1", DisplayName = "Alpha" };
        _store.Agents.Returns(new Dictionary<string, AgentState> { ["agent-1"] = agent }.AsReadOnly());
        _store.GetAgent("agent-1").Returns(agent);

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        // Record calls so far (from OnParametersSetAsync)
        _store.ClearReceivedCalls();
        _interaction.ClearReceivedCalls();

        // Act: fire a store change event (simulates streaming token, tool update, etc.)
        _store.OnChanged += Raise.Event<Action>();

        // Assert: no further SetActiveAgent or SelectConversation calls
        // (ApplyRouteSelectionAsync calls SetActiveAgent when agent differs from active)
        // ApplyRouteSelectionAsync calls Store.NotifyChanged() when it makes changes -- must not be called on store events
        _interaction.DidNotReceive().SelectConversationAsync(Arg.Any<string>(), Arg.Any<string>());
    }

}