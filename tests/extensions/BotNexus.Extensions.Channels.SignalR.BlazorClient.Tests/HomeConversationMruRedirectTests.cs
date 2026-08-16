using Bunit;
using Bunit.TestDoubles;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3064 AC2/AC3/AC4: an agent-only route resolves to an explicit conversation route via the MRU,
/// with <c>replace: true</c>, and a route that already names a conversation is never overridden.
/// </summary>
public sealed class HomeConversationMruRedirectTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store = Substitute.For<IClientStateStore>();
    private readonly IPortalLoadService _portalLoad = Substitute.For<IPortalLoadService>();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();
    private readonly ConversationMruService _mru = new();
    private readonly Dictionary<string, AgentState> _agents = [];

    public HomeConversationMruRedirectTests()
    {
        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _store.Agents.Returns(_ => _agents.AsReadOnly());
        _store.ActiveAgentId.Returns((string?)null);
        _store.When(s => s.SelectView(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SelectionSource>()))
            .Do(ci =>
            {
                var agentId = ci.ArgAt<string>(0);
                _store.ActiveAgentId.Returns(string.IsNullOrEmpty(agentId) ? null : agentId);
            });
        _store.GetAgent(Arg.Any<string>()).Returns(ci => _agents.GetValueOrDefault(ci.ArgAt<string>(0)));
        _store.GetStreamState(Arg.Any<string>()).Returns(new ConversationStreamState());

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton<IConversationMruService>(_mru);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private AgentState SeedAgent(string agentId, params string[] conversationIds)
    {
        var agent = new AgentState { AgentId = agentId, DisplayName = agentId };
        foreach (var conversationId in conversationIds)
            agent.Conversations[conversationId] = new ConversationState { ConversationId = conversationId, Title = conversationId };

        _agents[agentId] = agent;
        return agent;
    }

    private BunitNavigationManager Nav =>
        (BunitNavigationManager)_ctx.Services.GetRequiredService<NavigationManager>();

    // ── AC2 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Agent_only_route_with_a_matching_mru_entry_redirects_to_the_explicit_conversation_route()
    {
        SeedAgent("agent-1", "c-1", "c-2");
        _mru.Record("agent-1", "c-2");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Equal("http://localhost/agent/agent-1/conversation/c-2", Nav.Uri);
    }

    [Fact]
    public void Agent_only_route_redirect_url_encodes_agent_and_conversation_ids()
    {
        SeedAgent("agent/one", "conv id/one");
        _mru.Record("agent/one", "conv id/one");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, Uri.EscapeDataString("agent/one")));

        Assert.Equal(
            $"http://localhost/agent/{Uri.EscapeDataString("agent/one")}/conversation/{Uri.EscapeDataString("conv id/one")}",
            Nav.Uri);
    }

    [Fact]
    public void Agent_only_route_uses_only_that_agents_mru_entry()
    {
        // Per-agent partitioning at the redirect seam: another agent's navigation must not leak in.
        SeedAgent("agent-1", "c-1");
        SeedAgent("agent-2", "c-2");
        _mru.Record("agent-2", "c-2");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Empty(Nav.History);
    }

    // ── AC3 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_redirect_replaces_the_history_entry_so_the_redirecting_route_is_not_left_in_history()
    {
        // Without replace:true, Back lands on /agent/{id}, which immediately redirects forward again -
        // the user is trapped and can never navigate back past the agent-only route.
        SeedAgent("agent-1", "c-1");
        _mru.Record("agent-1", "c-1");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        var entry = Assert.Single(Nav.History);
        Assert.True(entry.Options.ReplaceHistoryEntry);
    }

    // ── AC4 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Route_naming_a_conversation_does_not_redirect_even_when_the_mru_holds_a_different_entry()
    {
        // The deep link is authoritative. A warm MRU from a prior soft navigation must not win.
        // ActiveConversationId is deliberately set to the MRU's entry, not the route's: that is the
        // case where consulting either ambient source would produce a DIFFERENT conversation from
        // the one the URL names, so it is the only fixture that can distinguish the two.
        var agent = SeedAgent("agent-1", "c-1", "c-2");
        agent.ActiveConversationId = "c-2";
        _mru.Record("agent-1", "c-2");

        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "c-1"));

        Assert.Empty(Nav.History);
        _interaction.Received(1).SelectConversationAsync("agent-1", "c-1");
        _interaction.DidNotReceive().SelectConversationAsync("agent-1", "c-2");
    }

    [Fact]
    public void Route_naming_a_conversation_does_not_redirect_to_ActiveConversationId()
    {
        // ActiveConversationId is the ambient value #3064 exists to stop consulting on this path.
        var agent = SeedAgent("agent-1", "c-1", "c-active");
        agent.ActiveConversationId = "c-active";

        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "c-1"));

        Assert.Empty(Nav.History);
    }

    [Fact]
    public void Navigating_to_an_explicit_conversation_route_records_it_in_the_mru()
    {
        // AC1's "populated on conversation selection": under #2247 every user-driven selection
        // navigates to the canonical route, so the route seam is the single MRU writer.
        SeedAgent("agent-1", "c-7");

        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "c-7"));

        Assert.Equal("c-7", _mru.GetMostRecent("agent-1"));
    }

    // ── AC6-adjacent: no redirect, no loop ──────────────────────────────────────────────────────

    [Fact]
    public void Agent_only_route_with_an_empty_mru_does_not_redirect()
    {
        SeedAgent("agent-1", "c-1");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Empty(Nav.History);
    }

    [Fact]
    public void Agent_with_no_conversations_at_all_does_not_redirect()
    {
        SeedAgent("agent-1");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Empty(Nav.History);
    }

    [Fact]
    public void A_stale_mru_entry_the_agent_no_longer_holds_does_not_redirect()
    {
        // An archived/deleted conversation would otherwise send the user to a dead route.
        SeedAgent("agent-1", "c-1");
        _mru.Record("agent-1", "c-gone");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Empty(Nav.History);
    }

    [Fact]
    public void The_redirect_happens_at_most_once_and_does_not_loop()
    {
        SeedAgent("agent-1", "c-1");
        _mru.Record("agent-1", "c-1");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));
        _store.OnChanged += Raise.Event<Action>();
        _store.OnChanged += Raise.Event<Action>();

        Assert.Single(Nav.History);
    }
}
