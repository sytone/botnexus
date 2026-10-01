using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3062: the chat and todo panes must bind to the conversation named by the ROUTE, the same way
/// the canvas pane already does since #2976. Before this change <c>ChatPanel</c> re-derived its
/// identity from <c>AgentState.ActiveConversationId</c> ambiently, so a deep link to
/// <c>/agent/{a}/conversation/{c}</c> could show the routed canvas beside a DIFFERENT conversation's
/// transcript, title, steering queue and paging.
///
/// The ownership guard on <c>AgentPanel.EffectiveConversationId</c> is load-bearing and is pinned
/// here for the chat pane too: <see cref="Home"/> renders one <c>AgentPanel</c> per agent in
/// <c>Store.Agents</c> while the route names exactly one agent, so a routed id must only ever be
/// adopted by the agent that actually owns that conversation.
/// </summary>
public sealed class ChatPanelRoutedConversationTests : IDisposable
{
    private const string ActiveTitle = "ACTIVE-CONVERSATION-TITLE";
    private const string RoutedTitle = "ROUTED-CONVERSATION-TITLE";
    private const string OtherAgentTitle = "OTHER-AGENT-CONVERSATION-TITLE";

    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IPortalLoadService _portalLoad = Substitute.For<IPortalLoadService>();

    public ChatPanelRoutedConversationTests()
    {
        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _store.SeedAgents([new AgentSummary("agent-1", "Alpha"), new AgentSummary("agent-2", "Beta")]);
        _store.SeedConversations("agent-1", [
            Conversation("conv-active", "agent-1", ActiveTitle, isDefault: false, isPinned: true),
            Conversation("conv-routed", "agent-1", RoutedTitle, isDefault: false, isPinned: false)
        ]);
        _store.SeedConversations("agent-2", [Conversation("conv-other", "agent-2", OtherAgentTitle, isDefault: true)]);

        // The divergence under test: the agent's ACTIVE conversation is conv-active while the route
        // names conv-routed. IAgentInteractionService is a substitute so Home's route-application
        // path cannot converge active onto routed - exactly the state a stale deep link produces.
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);
        _store.SetActiveConversation("agent-1", "conv-active");
        _store.SetActiveConversation("agent-2", "conv-other");

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        // #3064: Home injects the per-agent conversation MRU at the route seam.
        _ctx.Services.AddSingleton<IConversationMruService, ConversationMruService>();
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp =>
            new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static ConversationSummaryDto Conversation(
        string id,
        string agentId,
        string title,
        bool isDefault,
        bool isPinned = false) => new(
        ConversationId: id,
        AgentId: agentId,
        Title: title,
        IsDefault: isDefault,
        Status: "Active",
        ActiveSessionId: null,
        BindingCount: 0,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        IsPinned: isPinned,
        PinnedAt: isPinned ? DateTimeOffset.UtcNow : null);

    /// <summary>
    /// AC3: on <c>/agent/{a}/conversation/{c}</c> where <c>c</c> is NOT the agent's
    /// <c>ActiveConversationId</c>, the chat pane renders conversation <c>c</c>.
    /// </summary>
    [Fact]
    public void Chat_pane_renders_routed_conversation_when_it_differs_from_active()
    {
        var cut = RenderAt("/agent/agent-1/conversation/conv-routed", "agent-1", "conv-routed");

        var chat = ChatPaneMarkupFor(cut, "agent-1");
        Assert.Contains(RoutedTitle, chat);
        Assert.DoesNotContain(ActiveTitle, chat);
    }

    [Fact]
    public async Task Pin_action_retargets_the_new_route_and_rebuilds_both_pin_states_without_recreating_the_page()
    {
        var interaction = _ctx.Services.GetRequiredService<IAgentInteractionService>();
        var cut = RenderAt("/agent/agent-1/conversation/conv-active", "agent-1", "conv-active");

        PinActionLabel(cut, "conv-active").ShouldBe("Unpin");

        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/agent/agent-1/conversation/conv-routed");
        cut.Render(parameters => parameters
            .Add(component => component.AgentId, "agent-1")
            .Add(component => component.ConversationId, "conv-routed"));

        PinActionLabel(cut, "conv-routed").ShouldBe("Pin");
        var routedPanel = cut.FindComponents<ChatPanel>()
            .Single(component => component.Instance.ConversationId == "conv-routed");
        await cut.InvokeAsync(() =>
        {
            routedPanel.Find("[data-testid='conversation-actions-trigger']").Click();
            routedPanel.Find("[data-action-id='pin']").Click();
        });

        await interaction.Received(1).SetConversationPinnedAsync("agent-1", "conv-routed", true);
        await interaction.DidNotReceive().SetConversationPinnedAsync("agent-1", "conv-active", Arg.Any<bool>());

        _store.GetConversation("conv-active")!.IsPinned = false;
        _store.GetConversation("conv-routed")!.IsPinned = true;
        _store.SetActiveConversation("agent-1", "conv-active");
        cut.Render();

        PinActionLabel(cut, "conv-routed").ShouldBe("Unpin");
    }

    /// <summary>
    /// AC4: a routed id naming a conversation owned by a different agent is not adopted and does
    /// not fall back to ambient selected state on the explicit conversation route.
    /// </summary>
    [Fact]
    public void Chat_pane_does_not_adopt_a_routed_conversation_owned_by_another_agent()
    {
        var cut = RenderAt("/agent/agent-1/conversation/conv-other", "agent-1", "conv-other");

        var chat = ChatPaneMarkupFor(cut, "agent-1");
        Assert.DoesNotContain(ActiveTitle, chat);
        Assert.DoesNotContain(OtherAgentTitle, chat);
    }

    /// <summary>
    /// The cross-panel half of AC4: Home renders an AgentPanel for EVERY agent, but the routed
    /// conversation belongs to exactly one. A non-routed agent's chat pane must keep showing its own
    /// active conversation.
    /// </summary>
    [Fact]
    public void Routed_conversation_does_not_leak_into_a_different_agents_chat_pane()
    {
        var cut = RenderAt("/agent/agent-1/conversation/conv-routed", "agent-1", "conv-routed");

        var chat = ChatPaneMarkupFor(cut, "agent-2");
        Assert.Contains(OtherAgentTitle, chat);
        Assert.DoesNotContain(RoutedTitle, chat);
    }

    /// <summary>
    /// AC5 at the integration level: with no conversation segment on the route the routed id is
    /// null, resolution falls back to <c>ActiveConversationId</c>, and the pre-#3062 behaviour holds.
    /// </summary>
    [Fact]
    public void Chat_pane_falls_back_to_active_conversation_when_route_has_no_conversation()
    {
        var cut = RenderAt("/agent/agent-1", "agent-1", conversationId: null);

        var chat = ChatPaneMarkupFor(cut, "agent-1");
        Assert.Contains(ActiveTitle, chat);
        Assert.DoesNotContain(RoutedTitle, chat);
    }

    /// <summary>
    /// AC2 for the todo pane, which bypassed <c>EffectiveConversationId</c> entirely and read
    /// <c>Agent?.ActiveConversationId</c> directly.
    /// </summary>
    [Fact]
    public void Todo_pane_renders_the_routed_conversations_todo()
    {
        _store.GetConversation("conv-active")!.TodoJson = TodoJson("ACTIVE-TODO-ITEM");
        _store.GetConversation("conv-routed")!.TodoJson = TodoJson("ROUTED-TODO-ITEM");

        var cut = RenderAt("/agent/agent-1/conversation/conv-routed?tab=todo", "agent-1", "conv-routed");

        var todo = cut.Find("#agent-1-todo-panel").InnerHtml;
        Assert.Contains("ROUTED-TODO-ITEM", todo);
        Assert.DoesNotContain("ACTIVE-TODO-ITEM", todo);
    }

    private static string TodoJson(string text) =>
        $$"""{"items":[{"id":"1","text":"{{text}}","status":"pending"}]}""";

    private IRenderedComponent<Home> RenderAt(string uri, string agentId, string? conversationId)
    {
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(uri);

        return _ctx.Render<Home>(p =>
        {
            p.Add(c => c.AgentId, agentId);
            p.Add(c => c.ConversationId, conversationId);
        });
    }

    /// <summary>
    /// Scopes the assertion to ONE agent's conversation pane. Home renders several panels, so a
    /// whole-markup search could not tell them apart and the cross-agent assertions would be vacuous.
    /// </summary>
    private static string ChatPaneMarkupFor(IRenderedComponent<Home> cut, string agentId)
    {
        var section = cut.Find($"#{agentId}-conversation-panel");
        return section.InnerHtml;
    }

    private static string PinActionLabel(IRenderedComponent<Home> cut, string conversationId)
    {
        var panel = cut.FindComponents<ChatPanel>()
            .Single(component => component.Instance.ConversationId == conversationId);
        panel.Find("[data-testid='conversation-actions-trigger']").Click();
        var label = panel.Find("[data-action-id='pin']").TextContent.Trim();
        panel.Find("[data-testid='conversation-action-menu-dismiss']").Click();
        return label;
    }
}
