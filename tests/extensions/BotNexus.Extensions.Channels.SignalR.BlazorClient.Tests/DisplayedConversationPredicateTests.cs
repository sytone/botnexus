using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3212: pins <see cref="IDisplayedConversation"/> as implemented by <see cref="ClientStateStore"/>.
/// The predicate answers from the single route-written <see cref="ViewSelection"/>, NOT from the
/// per-agent <see cref="AgentState.ActiveConversationId"/> marker — which is exactly what makes it
/// a visibility answer instead of a "was this ever selected" answer.
/// </summary>
public sealed class DisplayedConversationPredicateTests
{
    private static ClientStateStore CreateStore()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Alpha"), new AgentSummary("a-2", "Beta")]);

        var a1 = store.GetAgent("a-1")!;
        a1.Conversations["c-1"] = new ConversationState { ConversationId = "c-1", Title = "One" };
        a1.Conversations["c-2"] = new ConversationState { ConversationId = "c-2", Title = "Two" };

        var a2 = store.GetAgent("a-2")!;
        a2.Conversations["c-9"] = new ConversationState { ConversationId = "c-9", Title = "Nine" };

        return store;
    }

    [Fact]
    public void The_route_selected_pair_is_displayed()
    {
        var store = CreateStore();
        store.SelectView("a-1", "c-1", SelectionSource.RouteNavigation);

        Assert.True(store.IsConversationDisplayed("a-1", "c-1"));
        Assert.True(store.IsAgentDisplayed("a-1"));
        Assert.Equal("c-1", store.DisplayedConversationIdFor("a-1"));
    }

    [Fact]
    public void A_second_conversation_of_the_displayed_agent_is_not_displayed()
    {
        var store = CreateStore();
        store.SelectView("a-1", "c-1", SelectionSource.RouteNavigation);

        Assert.False(store.IsConversationDisplayed("a-1", "c-2"));
        // The AGENT is still displayed even though this conversation is not — that distinction is
        // what keeps the agent badge and the conversation badge on separate, correct answers.
        Assert.True(store.IsAgentDisplayed("a-1"));
    }

    [Fact]
    public void Another_agents_conversation_is_never_displayed()
    {
        var store = CreateStore();
        store.SelectView("a-1", "c-1", SelectionSource.RouteNavigation);

        Assert.False(store.IsConversationDisplayed("a-2", "c-9"));
        Assert.False(store.IsAgentDisplayed("a-2"));
        Assert.Null(store.DisplayedConversationIdFor("a-2"));
    }

    [Fact]
    public void The_per_agent_ActiveConversationId_marker_does_not_make_a_conversation_displayed()
    {
        // The core #3061 defect in one assertion: every agent carries its own last-selected
        // marker, so consulting it made N conversations simultaneously "active" while the browser
        // rendered exactly one.
        var store = CreateStore();
        store.SelectView("a-1", "c-1", SelectionSource.RouteNavigation);
        store.GetAgent("a-2")!.ActiveConversationId = "c-9";

        Assert.Equal("c-9", store.GetAgent("a-2")!.ActiveConversationId);
        Assert.False(store.IsConversationDisplayed("a-2", "c-9"));
    }

    [Fact]
    public void A_null_or_blank_id_is_never_displayed()
    {
        var store = CreateStore();
        store.SelectView("a-1", "c-1", SelectionSource.RouteNavigation);

        Assert.False(store.IsConversationDisplayed(null, "c-1"));
        Assert.False(store.IsConversationDisplayed("a-1", null));
        Assert.False(store.IsConversationDisplayed("a-1", ""));
        Assert.False(store.IsAgentDisplayed(null));
        Assert.Null(store.DisplayedConversationIdFor(null));
    }

    [Fact]
    public void An_agent_only_route_displays_the_agent_but_no_conversation()
    {
        // /agent/{id} with no conversation segment: the agent's pane is on screen but no
        // conversation is named by the route, so nothing is conversation-displayed.
        var store = CreateStore();
        store.SelectView("a-1", string.Empty, SelectionSource.RouteNavigation);

        Assert.True(store.IsAgentDisplayed("a-1"));
        Assert.Null(store.DisplayedConversationIdFor("a-1"));
        Assert.False(store.IsConversationDisplayed("a-1", "c-1"));
    }

    [Fact]
    public void Selecting_a_conversation_on_the_displayed_agent_clears_its_unread()
    {
        var store = CreateStore();
        store.SelectView("a-1", "c-1", SelectionSource.RouteNavigation);
        store.GetAgent("a-1")!.Conversations["c-2"].UnreadCount = 4;

        store.SetActiveConversation("a-1", "c-2");

        // a-1 is the displayed agent, so moving it to c-2 makes c-2 displayed and read.
        Assert.True(store.IsConversationDisplayed("a-1", "c-2"));
        Assert.Equal(0, store.GetConversation("c-2")!.UnreadCount);
    }

    [Fact]
    public void Selecting_a_conversation_on_a_NON_displayed_agent_does_not_clear_its_unread()
    {
        // #3212 behaviour change, stated deliberately: a background agent's selection change is
        // not the user reading anything. Zeroing the badge there would hide messages the user has
        // never seen.
        var store = CreateStore();
        store.SelectView("a-1", "c-1", SelectionSource.RouteNavigation);
        store.GetAgent("a-2")!.Conversations["c-9"].UnreadCount = 4;

        store.SetActiveConversation("a-2", "c-9");

        Assert.False(store.IsConversationDisplayed("a-2", "c-9"));
        Assert.Equal(4, store.GetConversation("c-9")!.UnreadCount);
    }
}
