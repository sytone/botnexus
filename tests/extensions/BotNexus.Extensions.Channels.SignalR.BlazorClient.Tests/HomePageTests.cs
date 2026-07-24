using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class HomePageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IPortalLoadService _portalLoad;
    private readonly IAgentInteractionService _interaction;

    public HomePageTests()
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

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Shows_connecting_spinner_when_not_ready()
    {
        _portalLoad.IsReady.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);

        var cut = _ctx.Render<Home>();

        Assert.Contains("Connecting", cut.Markup);
    }

    [Fact]
    public void Shows_portal_loading_container_when_not_ready()
    {
        _portalLoad.IsReady.Returns(false);

        var cut = _ctx.Render<Home>();

        cut.Find(".portal-loading");
    }

    [Fact]
    public void Shows_error_message_when_LoadError_is_set()
    {
        _portalLoad.IsReady.Returns(false);
        _portalLoad.LoadError.Returns("Connection refused");

        var cut = _ctx.Render<Home>();

        Assert.Contains("Connection refused", cut.Markup);
        cut.Find(".portal-load-error");
    }

    [Fact]
    public void Does_not_render_spinner_when_error_is_set()
    {
        _portalLoad.IsReady.Returns(false);
        _portalLoad.LoadError.Returns("Something failed");

        var cut = _ctx.Render<Home>();

        Assert.Empty(cut.FindAll(".portal-load-spinner"));
    }

    [Fact]
    public void Renders_main_UI_when_ready_with_no_active_agent()
    {
        _portalLoad.IsReady.Returns(true);
        _store.ActiveAgentId.Returns((string?)null);

        var cut = _ctx.Render<Home>();

        Assert.Empty(cut.FindAll(".portal-loading"));
        cut.Find(".agent-dashboard");
    }

    [Fact]
    public void Does_not_render_loading_when_portal_is_ready()
    {
        _portalLoad.IsReady.Returns(true);

        var cut = _ctx.Render<Home>();

        Assert.Empty(cut.FindAll(".portal-loading"));
    }

    [Fact]
    public void Renders_chat_panel_wrapper_for_each_agent_when_ready()
    {
        _portalLoad.IsReady.Returns(true);
        var agents = new Dictionary<string, AgentState>
        {
            ["agent-1"] = new AgentState { AgentId = "agent-1", DisplayName = "Alpha" },
            ["agent-2"] = new AgentState { AgentId = "agent-2", DisplayName = "Beta" }
        };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent(Arg.Any<string>()).Returns(ci =>
            agents.GetValueOrDefault(ci.ArgAt<string>(0)));

        // Register additional services needed by ChatPanel
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());

        var cut = _ctx.Render<Home>();

        var wrappers = cut.FindAll(".chat-panel-wrapper");
        Assert.Equal(2, wrappers.Count);
    }

    [Fact]
    public void Active_agent_wrapper_has_active_class()
    {
        _portalLoad.IsReady.Returns(true);
        var agents = new Dictionary<string, AgentState>
        {
            ["agent-1"] = new AgentState { AgentId = "agent-1", DisplayName = "Alpha" }
        };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agents["agent-1"]);

        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());

        var cut = _ctx.Render<Home>();

        var wrapper = cut.Find(".chat-panel-wrapper");
        Assert.Contains("active", wrapper.ClassList);
    }

    [Fact]
    public void Applies_agent_route_when_portal_becomes_ready()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["agent-1"] = new() { AgentId = "agent-1", DisplayName = "Alpha" },
            ["agent-2"] = new() { AgentId = "agent-2", DisplayName = "Beta" }
        };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent(Arg.Any<string>()).Returns(ci =>
            agents.GetValueOrDefault(ci.ArgAt<string>(0)));
        _portalLoad.IsReady.Returns(false, true);
        _portalLoad.IsLoading.Returns(true, false);

        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-2"));

        _portalLoad.OnReadyChanged += Raise.Event<Action>();

        Assert.Equal("agent-2", _store.ActiveAgentId);
    }

    [Fact]
    public void Direct_route_to_chat_agent_and_conversation_selects_conversation_when_available()
    {
        _portalLoad.IsReady.Returns(true);

        var targetAgent = new AgentState
        {
            AgentId = "agent-2",
            DisplayName = "Beta",
            ActiveConversationId = "c-1"
        };
        targetAgent.Conversations["c-1"] = new ConversationState { ConversationId = "c-1", Title = "One" };
        targetAgent.Conversations["c-2"] = new ConversationState { ConversationId = "c-2", Title = "Two" };
        var agents = new Dictionary<string, AgentState>
        {
            ["agent-2"] = targetAgent
        };

        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent(Arg.Any<string>()).Returns(ci =>
            agents.GetValueOrDefault(ci.ArgAt<string>(0)));

        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());

        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-2")
            .Add(c => c.ConversationId, "c-2"));

        Assert.Equal("agent-2", _store.ActiveAgentId);
        _interaction.Received(1).SelectConversationAsync("agent-2", "c-2");
    }

    [Fact]
    public void Direct_route_to_chat_agent_only_notifies_layout_subscribers_on_agent_restore()
    {
        _portalLoad.IsReady.Returns(true);
        _store.ActiveAgentId.Returns("agent-1");

        var agents = new Dictionary<string, AgentState>
        {
            ["agent-1"] = new() { AgentId = "agent-1", DisplayName = "Alpha" },
            ["agent-2"] = new() { AgentId = "agent-2", DisplayName = "Beta" }
        };

        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent(Arg.Any<string>()).Returns(ci =>
            agents.GetValueOrDefault(ci.ArgAt<string>(0)));

        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-2"));

        Assert.Equal("agent-2", _store.ActiveAgentId);
        _store.Received(1).NotifyChanged();
    }

    [Fact]
    public void Direct_route_decodes_url_encoded_agent_and_conversation_ids()
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
        _store.GetAgent(Arg.Any<string>()).Returns(ci =>
            agents.GetValueOrDefault(ci.ArgAt<string>(0)));

        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());

        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, encodedAgentId)
            .Add(c => c.ConversationId, encodedConversationId));

        Assert.Equal(decodedAgentId, _store.ActiveAgentId);
        _interaction.Received(1).SelectConversationAsync(decodedAgentId, decodedConversationId);
    }

    [Fact]
    public void Direct_route_with_stale_ids_falls_back_without_crashing()
    {
        _portalLoad.IsReady.Returns(true);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        var knownAgent = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            ActiveConversationId = "known-conversation"
        };
        knownAgent.Conversations["known-conversation"] = new ConversationState
        {
            ConversationId = "known-conversation",
            Title = "Known"
        };

        var agents = new Dictionary<string, AgentState>
        {
            ["agent-1"] = knownAgent
        };

        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent(Arg.Any<string>()).Returns(ci =>
            agents.GetValueOrDefault(ci.ArgAt<string>(0)));

        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());

        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "missing-agent")
            .Add(c => c.ConversationId, "missing-conversation"));

        Assert.Equal("agent-1", _store.ActiveAgentId);
        _interaction.DidNotReceive().SelectConversationAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void Direct_route_with_missing_agent_sets_fallback_and_notifies_when_active_agent_is_empty()
    {
        _portalLoad.IsReady.Returns(true);
        _store.ActiveAgentId.Returns((string?)null);

        var fallbackAgent = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            ActiveConversationId = "known-conversation"
        };
        fallbackAgent.Conversations["known-conversation"] = new ConversationState
        {
            ConversationId = "known-conversation",
            Title = "Known"
        };

        var agents = new Dictionary<string, AgentState>
        {
            ["agent-1"] = fallbackAgent
        };

        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent(Arg.Any<string>()).Returns(ci =>
            agents.GetValueOrDefault(ci.ArgAt<string>(0)));

        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());

        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "missing-agent")
            .Add(c => c.ConversationId, "missing-conversation"));

        Assert.Equal("agent-1", _store.ActiveAgentId);
        _store.Received(1).NotifyChanged();
        _interaction.DidNotReceive().SelectConversationAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    // ── #2247: route-owned view via the canonical /agent/{id}/conversation/{id} shape ──────────

    [Fact]
    public void Canonical_agent_conversation_route_restores_agent_and_selects_conversation()
    {
        // #2247: a deep-link / refresh onto the canonical route-owned shape must restore exactly that
        // view. Home binds the SAME AgentId/ConversationId parameters for the /agent/{id}/conversation/{id}
        // route as for the legacy /chat route, so the restore path is identical - proving the new shape
        // drives SelectView(RouteNavigation) and conversation selection.
        _portalLoad.IsReady.Returns(true);

        var targetAgent = new AgentState
        {
            AgentId = "farnsworth",
            DisplayName = "Farnsworth",
            ActiveConversationId = "c-1"
        };
        targetAgent.Conversations["c-1"] = new ConversationState { ConversationId = "c-1", Title = "One" };
        targetAgent.Conversations["c-99"] = new ConversationState { ConversationId = "c-99", Title = "Deep" };
        var agents = new Dictionary<string, AgentState> { ["farnsworth"] = targetAgent };

        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent(Arg.Any<string>()).Returns(ci => agents.GetValueOrDefault(ci.ArgAt<string>(0)));

        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());

        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "farnsworth")
            .Add(c => c.ConversationId, "c-99"));

        Assert.Equal("farnsworth", _store.ActiveAgentId);
        _store.Received().SelectView("farnsworth", string.Empty, SelectionSource.RouteNavigation);
        _interaction.Received(1).SelectConversationAsync("farnsworth", "c-99");
    }

    [Fact]
    public void Route_restore_only_ever_uses_RouteNavigation_source_never_UserClick_or_SubAgentView()
    {
        // #2247 seam guard: the route-restore path must tag its selection as RouteNavigation. It must
        // never impersonate a UserClick or a SubAgentView, since those carry different authority in the
        // store's anti-hijack guard. This pins the intent-source contract for deep-link/refresh/back.
        _portalLoad.IsReady.Returns(true);

        var agent = new AgentState { AgentId = "farnsworth", DisplayName = "Farnsworth" };
        var agents = new Dictionary<string, AgentState> { ["farnsworth"] = agent };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent(Arg.Any<string>()).Returns(ci => agents.GetValueOrDefault(ci.ArgAt<string>(0)));

        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "farnsworth"));

        _store.Received().SelectView("farnsworth", string.Empty, SelectionSource.RouteNavigation);
        _store.DidNotReceive().SelectView("farnsworth", Arg.Any<string>(), SelectionSource.UserClick);
        _store.DidNotReceive().SelectView("farnsworth", Arg.Any<string>(), SelectionSource.SubAgentView);
    }
}