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

    /// <summary>
    /// Seed one conversation with the axes cold-start resolution actually reads. Every parameter is
    /// explicit so a fixture cannot accidentally rely on a default that happens to make the ordering
    /// come out right.
    /// </summary>
    private ConversationState SeedConversation(
        string agentId,
        string conversationId,
        DateTimeOffset updatedAt,
        bool isPinned = false,
        string status = "Active",
        ConversationVisibility visibility = ConversationVisibility.UserFacing,
        bool isDefault = false)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
        {
            agent = new AgentState { AgentId = agentId, DisplayName = agentId };
            _agents[agentId] = agent;
        }

        var conversation = new ConversationState
        {
            ConversationId = conversationId,
            Title = conversationId,
            UpdatedAt = updatedAt,
            IsPinned = isPinned,
            Status = status,
            Visibility = visibility,
            IsDefault = isDefault,
        };

        agent.Conversations[conversationId] = conversation;
        return conversation;
    }

    private static DateTimeOffset At(int minutes) => new(2026, 1, 1, 0, minutes, 0, TimeSpan.Zero);

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
    public void Agent_only_route_ignores_another_agents_mru_entry_and_resolves_from_its_own_conversations()
    {
        // Per-agent partitioning at the redirect seam: another agent's navigation must not leak in.
        // #3218 changes the OUTCOME, not the property: agent-1 no longer sits on the agent-only route,
        // it cold-starts onto its OWN conversation. The leak this test exists to catch would land the
        // user on c-2, which is asserted against explicitly.
        SeedAgent("agent-1", "c-1");
        SeedAgent("agent-2", "c-2");
        _mru.Record("agent-2", "c-2");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Equal("http://localhost/agent/agent-1/conversation/c-1", Nav.Uri);
        Assert.DoesNotContain("c-2", Nav.Uri, StringComparison.Ordinal);
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
    public void Agent_with_no_conversations_at_all_does_not_redirect()
    {
        SeedAgent("agent-1");

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

    // ── #3218 AC1: cold start resolves from server state when the MRU is empty ──────────────────

    [Fact]
    public void Cold_start_with_an_empty_mru_redirects_to_the_most_recently_updated_active_conversation()
    {
        SeedConversation("agent-1", "c-old", At(1));
        SeedConversation("agent-1", "c-recent", At(9));
        SeedConversation("agent-1", "c-middle", At(5));

        Assert.Null(_mru.GetMostRecent("agent-1"));

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Equal("http://localhost/agent/agent-1/conversation/c-recent", Nav.Uri);
    }

    [Fact]
    public void A_warm_mru_entry_still_wins_over_cold_start_resolution()
    {
        // Cold start is a FALLBACK, not a replacement: where this circuit has actually navigated is a
        // stronger signal than server recency. c-recent is strictly more recent, so a resolver that
        // ignored the MRU would land there and fail this.
        SeedConversation("agent-1", "c-visited", At(1));
        SeedConversation("agent-1", "c-recent", At(9));
        _mru.Record("agent-1", "c-visited");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Equal("http://localhost/agent/agent-1/conversation/c-visited", Nav.Uri);
    }

    [Fact]
    public void A_stale_mru_entry_the_agent_no_longer_holds_falls_back_to_cold_start_resolution()
    {
        // #3064 left this as "no redirect" because there was no second source to fall back to. With
        // cold start present, a stale entry must resolve to a LIVE conversation - never to the dead
        // route c-gone.
        SeedConversation("agent-1", "c-1", At(3));
        _mru.Record("agent-1", "c-gone");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Equal("http://localhost/agent/agent-1/conversation/c-1", Nav.Uri);
        Assert.DoesNotContain("c-gone", Nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Cold_start_redirect_url_encodes_agent_and_conversation_ids()
    {
        SeedConversation("agent/one", "conv id/one", At(4));

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, Uri.EscapeDataString("agent/one")));

        Assert.Equal(
            $"http://localhost/agent/{Uri.EscapeDataString("agent/one")}/conversation/{Uri.EscapeDataString("conv id/one")}",
            Nav.Uri);
    }

    // ── #3218 AC2: a pin beats a more recently updated unpinned conversation ────────────────────

    [Fact]
    public void Cold_start_prefers_a_pinned_conversation_over_a_more_recently_updated_unpinned_one()
    {
        // The exact ordering CONFLICT: the pinned conversation is strictly OLDER, so recency alone
        // would pick c-unpinned. Only a resolver that ranks the pin above recency passes.
        SeedConversation("agent-1", "c-pinned", At(1), isPinned: true);
        SeedConversation("agent-1", "c-unpinned", At(9));

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Equal("http://localhost/agent/agent-1/conversation/c-pinned", Nav.Uri);
    }

    [Fact]
    public void Cold_start_picks_the_most_recent_among_several_pinned_conversations()
    {
        // Guards the degenerate fix for the clause above: "always take the first pinned one" would
        // pass the conflict test while ignoring recency entirely.
        SeedConversation("agent-1", "c-pinned-old", At(1), isPinned: true);
        SeedConversation("agent-1", "c-pinned-new", At(5), isPinned: true);
        SeedConversation("agent-1", "c-unpinned", At(9));

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Equal("http://localhost/agent/agent-1/conversation/c-pinned-new", Nav.Uri);
    }

    // ── #3218 AC3: internal and archived conversations are not candidates ───────────────────────

    [Fact]
    public void Cold_start_ignores_runtime_internal_conversations()
    {
        // The internal thread is the most recent AND pinned - it would win on every other axis.
        SeedConversation("agent-1", "c-internal", At(9), isPinned: true,
            visibility: ConversationVisibility.InternalHidden);
        SeedConversation("agent-1", "c-user", At(2));

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Equal("http://localhost/agent/agent-1/conversation/c-user", Nav.Uri);
    }

    [Fact]
    public void Cold_start_ignores_archived_conversations()
    {
        SeedConversation("agent-1", "c-archived", At(9), isPinned: true, status: "Archived");
        SeedConversation("agent-1", "c-active", At(2));

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Equal("http://localhost/agent/agent-1/conversation/c-active", Nav.Uri);
    }

    // ── #3218 AC4: same history policy, fires at most once ─────────────────────────────────────

    [Fact]
    public void The_cold_start_redirect_replaces_the_history_entry()
    {
        SeedConversation("agent-1", "c-1", At(1));

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));

        var entry = Assert.Single(Nav.History);
        Assert.True(entry.Options.ReplaceHistoryEntry);
    }

    [Fact]
    public void The_cold_start_redirect_happens_at_most_once_and_does_not_loop()
    {
        SeedConversation("agent-1", "c-1", At(1));

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));
        _store.OnChanged += Raise.Event<Action>();
        _store.OnChanged += Raise.Event<Action>();
        _store.OnChanged += Raise.Event<Action>();

        Assert.Single(Nav.History);
    }

    // ── #3218 AC5: nothing eligible means no redirect and no loop ───────────────────────────────

    [Fact]
    public void An_agent_whose_only_conversations_are_archived_or_internal_does_not_redirect_and_does_not_loop()
    {
        // The sad path for cold start: candidates EXIST but none is eligible. A resolver that fell
        // back to "just take the first conversation" would redirect here.
        SeedConversation("agent-1", "c-archived", At(9), status: "Archived");
        SeedConversation("agent-1", "c-internal", At(8), visibility: ConversationVisibility.InternalHidden);

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));
        _store.OnChanged += Raise.Event<Action>();
        _store.OnChanged += Raise.Event<Action>();

        Assert.Empty(Nav.History);
    }

    [Fact]
    public void An_agent_with_no_conversations_does_not_redirect_and_does_not_loop_across_repeated_state_changes()
    {
        SeedAgent("agent-1");

        _ctx.Render<Home>(p => p.Add(c => c.AgentId, "agent-1"));
        for (var i = 0; i < 5; i++)
            _store.OnChanged += Raise.Event<Action>();

        Assert.Empty(Nav.History);
    }

    // ── #3218 AC6: archiving the displayed conversation performs no navigation ──────────────────

    [Fact]
    public void Archiving_the_currently_displayed_conversation_does_not_navigate()
    {
        // Lazy convergence: the gateway never pushes a navigation. The user keeps reading the
        // conversation they were on; the LIST is what converges, on the next navigation.
        var displayed = SeedConversation("agent-1", "c-open", At(5));
        SeedConversation("agent-1", "c-other", At(9));
        _agents["agent-1"].ActiveConversationId = "c-open";

        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "c-open"));

        var uriBefore = Nav.Uri;
        Assert.Empty(Nav.History);

        // The server archives the conversation the user is sitting on and announces it.
        displayed.Status = "Archived";
        _store.OnChanged += Raise.Event<Action>();
        _store.OnChanged += Raise.Event<Action>();

        // No navigation was performed, and the browser's location is byte-for-byte what it was.
        // Note the assertion is deliberately NOT "Nav.Uri ends in /conversation/c-open": bUnit
        // supplies route parameters directly rather than by parsing a URL, so an un-navigated
        // circuit sits at the base uri. Asserting the route SHAPE here would be asserting a
        // property of the harness; Nav.History is what actually records a navigation, and it is
        // still empty.
        Assert.Empty(Nav.History);
        Assert.Equal(uriBefore, Nav.Uri);
        _interaction.DidNotReceive().SelectConversationAsync("agent-1", "c-other");
    }

    [Fact]
    public void The_conversation_list_converges_on_the_next_navigation_after_an_archive()
    {
        // The other half of convergence: once the user DOES navigate away to the agent-only route,
        // cold-start resolution no longer offers the archived conversation.
        var displayed = SeedConversation("agent-1", "c-open", At(5));
        SeedConversation("agent-1", "c-other", At(2));

        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "c-open"));

        displayed.Status = "Archived";

        // Next navigation: a fresh circuit lands on the agent-only route with a cold MRU.
        var resolved = PortalListOrdering.ResolveColdStartConversation(_agents["agent-1"].Conversations.Values);

        Assert.NotNull(resolved);
        Assert.Equal("c-other", resolved!.ConversationId);
    }
}
