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
        // The real store derives ActiveAgentId from the single ViewSelection SelectView writes.
        // Mirror that on the substitute so route/bootstrap assertions observe the selected agent.
        _store.When(s => s.SelectView(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SelectionSource>()))
            .Do(ci =>
            {
                var agentId = ci.ArgAt<string>(0);
                _store.ActiveAgentId.Returns(string.IsNullOrEmpty(agentId) ? null : agentId);
            });
        _store.GetStreamState(Arg.Any<string>()).Returns(new ConversationStreamState());
        _store.GetMessages(Arg.Any<string>()).Returns(new List<ChatMessage>().AsReadOnly());

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(new BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services.MobileHubTuningOptions());
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

    // -- #1475: user messages render through the Markdown pipeline on mobile -----

    [Fact]
    public void Renders_user_message_as_markdown_markup_on_mobile()
    {
        _portalLoad.IsReady.Returns(true);
        var agent = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            ActiveConversationId = "conv-1",
            IsConnected = true
        };
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", Title = "C" };
        _store.Agents.Returns(new Dictionary<string, AgentState> { ["agent-1"] = agent }.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agent);
        var messages = new List<ChatMessage> { new("user", "**bold**", DateTimeOffset.UtcNow) };
        _store.GetMessages("conv-1").Returns(messages.AsReadOnly());
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true).SetResult("<p><strong>bold</strong></p>");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));
        // Markdown caching runs in response to store changes (streaming/load), so fire one.
        _store.OnChanged += Raise.Event<Action>();

        // The render markdown JS must be invoked for the user message...
        Assert.Contains(_ctx.JSInterop.Invocations, i =>
            i.Identifier == "BotNexus.renderMarkdown"
            && i.Arguments.Count == 1
            && i.Arguments[0] is string s && s == "**bold**");
        // ...and the bubble renders the sanitized HTML rather than the raw markdown source.
        Assert.Contains("<strong>bold</strong>", cut.Markup);
        Assert.DoesNotContain("**bold**", cut.Markup);
    }

    [Fact]
    public void Does_not_render_system_message_as_markdown_markup_on_mobile()
    {
        _portalLoad.IsReady.Returns(true);
        var agent = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            ActiveConversationId = "conv-1",
            IsConnected = true
        };
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", Title = "C" };
        _store.Agents.Returns(new Dictionary<string, AgentState> { ["agent-1"] = agent }.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agent);
        var messages = new List<ChatMessage> { new("system", "**not rendered**", DateTimeOffset.UtcNow) };
        _store.GetMessages("conv-1").Returns(messages.AsReadOnly());
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true).SetResult("<p><strong>not rendered</strong></p>");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));
        _store.OnChanged += Raise.Event<Action>();

        // System messages stay on raw text rendering.
        Assert.Contains("**not rendered**", cut.Markup);
    }

    // -- #2069: mobile Markdown and plain-text whitespace -----------------------

    [Fact]
    public void Persisted_markdown_uses_markdown_whitespace_surface()
    {
        ConfigureReadyConversation(new ChatMessage("assistant", "line one`nline two`n`nnext paragraph", DateTimeOffset.UtcNow));
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true)
            .SetResult("<p>line one<br>line two</p>`n<p>next paragraph</p>`n");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));
        _store.OnChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
        {
            var content = cut.Find(".message-content.markdown-content");
            content.InnerHtml.ShouldContain("<p>line one<br>line two</p>");
            content.ClassList.ShouldNotContain("plain-text-content");
        });
    }

    [Fact]
    public void Plain_text_fallback_preserves_intentional_whitespace_surface()
    {
        ConfigureReadyConversation(new ChatMessage("system", "line one`n  indented", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        var content = cut.Find(".message-content.plain-text-content");
        content.TextContent.ShouldBe("line one`n  indented");
        content.ClassList.ShouldNotContain("markdown-content");
    }

    [Fact]
    public void Live_stream_uses_plain_text_whitespace_surface()
    {
        ConfigureReadyConversation();
        _store.GetStreamState("conv-1").Returns(new ConversationStreamState
        {
            IsStreaming = true,
            Buffer = "line one`n  indented"
        });

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        var content = cut.Find(".message.streaming .message-content.plain-text-content");
        content.TextContent.ShouldContain("line one`n  indented");
        content.ClassList.ShouldNotContain("markdown-content");
    }

    private void ConfigureReadyConversation(params ChatMessage[] messages)
    {
        _portalLoad.IsReady.Returns(true);
        var agent = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            ActiveConversationId = "conv-1",
            IsConnected = true
        };
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", Title = "C" };
        _store.Agents.Returns(new Dictionary<string, AgentState> { ["agent-1"] = agent }.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agent);
        _store.GetMessages("conv-1").Returns(messages.ToList().AsReadOnly());
    }

    // -- #1691: scroll-up history pagination (load-more) ---------------------

    [Fact]
    public void Mobile_load_more_affordance_is_shown_when_more_history_exists()
    {
        _portalLoad.IsReady.Returns(true);
        var agent = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            ActiveConversationId = "conv-1",
            IsConnected = true
        };
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "C",
            HistoryLoaded = true,
            HasMoreHistory = true,
            LoadedHistoryRows = 20
        };
        _store.Agents.Returns(new Dictionary<string, AgentState> { ["agent-1"] = agent }.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agent);
        _store.GetMessages("conv-1").Returns(new List<ChatMessage>().AsReadOnly());

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.NotNull(cut.Find("[data-testid='mobile-load-more']"));
    }

    [Fact]
    public async Task Mobile_scroll_to_top_fetches_next_page_via_shared_service()
    {
        // #1691: mobile and desktop share AgentInteractionService.LoadMoreHistoryAsync. The mobile
        // scroll observer fires OnScrolledToTop, which must delegate to that single implementation.
        _portalLoad.IsReady.Returns(true);
        var agent = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            ActiveConversationId = "conv-1",
            IsConnected = true
        };
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "C",
            HistoryLoaded = true,
            HasMoreHistory = true,
            LoadedHistoryRows = 20
        };
        _store.Agents.Returns(new Dictionary<string, AgentState> { ["agent-1"] = agent }.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agent);
        _store.GetMessages("conv-1").Returns(new List<ChatMessage>().AsReadOnly());
        _interaction.LoadMoreHistoryAsync("agent-1", "conv-1").Returns(20);

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        await cut.InvokeAsync(() => cut.Instance.OnScrolledToTop());

        await _interaction.Received(1).LoadMoreHistoryAsync("agent-1", "conv-1");
    }

}