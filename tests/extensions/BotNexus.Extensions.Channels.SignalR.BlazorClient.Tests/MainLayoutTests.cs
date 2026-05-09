using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class MainLayoutTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;
    private readonly IAgentInteractionService _interaction;
    private readonly IPortalLoadService _portalLoad;

    public MainLayoutTests()
    {
        _store = new ClientStateStore();
        _interaction = Substitute.For<IAgentInteractionService>();
        _portalLoad = Substitute.For<IPortalLoadService>();

        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);

        var hub = new GatewayHubConnection();
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var gatewayInfo = new GatewayInfoService(http, restClient);
        var featureFlags = new FeatureFlagsService(_ctx.JSInterop.JSRuntime);

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(hub);
        _ctx.Services.AddSingleton(gatewayInfo);
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        _ctx.Services.AddSingleton(featureFlags);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(http);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<MainLayout> RenderLayout() =>
        _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (Microsoft.AspNetCore.Components.RenderFragment)(_ => { })));

    [Fact]
    public void Renders_app_shell_container()
    {
        var cut = RenderLayout();
        cut.Find(".app-shell");
    }

    [Fact]
    public void Renders_sidebar_closed_by_default()
    {
        var cut = RenderLayout();
        cut.Find(".sidebar-closed");
    }

    [Fact]
    public void Burger_button_is_present()
    {
        var cut = RenderLayout();
        var burger = cut.Find(".burger-btn");
        Assert.NotNull(burger);
    }

    [Fact]
    public void Clicking_burger_opens_sidebar()
    {
        var cut = RenderLayout();

        cut.Find(".burger-btn").Click();

        cut.Find(".sidebar-open");
    }

    [Fact]
    public void Clicking_burger_twice_closes_sidebar()
    {
        var cut = RenderLayout();

        cut.Find(".burger-btn").Click();
        cut.Find(".burger-btn").Click();

        cut.Find(".sidebar-closed");
    }

    [Fact]
    public void Does_not_show_agent_dropdown_when_no_agents()
    {
        var cut = RenderLayout();
        Assert.Empty(cut.FindAll(".agent-dropdown-select"));
    }

    [Fact]
    public void Shows_agent_dropdown_when_agents_are_seeded()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.NotifyChanged();

        var cut = RenderLayout();

        cut.Find(".agent-dropdown-select");
    }

    [Fact]
    public void Shows_agent_display_name_in_dropdown()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha Agent")]);
        _store.NotifyChanged();

        var cut = RenderLayout();

        Assert.Contains("Alpha Agent", cut.Markup);
    }

    [Fact]
    public void New_conversation_button_is_present_when_agent_is_active()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", []);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        cut.Find(".conversation-new-btn");
    }

    [Fact]
    public void Shows_conversation_list_when_agent_has_conversations()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Chat 1", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        Assert.Contains("Chat 1", cut.Markup);
    }

    [Fact]
    public void Shows_default_badge_on_default_conversation()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Default Chat", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        cut.Find(".conversation-default-badge");
    }

    [Fact]
    public void Shows_unread_dot_when_conversation_has_unread_messages()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Active Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";
        var conv = _store.GetAgent("a-1")!.Conversations["c-1"];
        conv.UnreadCount = 3;

        var cut = RenderLayout();

        cut.Find(".conversation-unread-dot");
    }

    [Fact]
    public void Active_conversation_has_active_css_class()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Active Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SetActiveConversation("a-1", "c-1");
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        var activeConv = cut.Find(".conversation-list-item-btn.active");
        Assert.NotNull(activeConv);
    }

    [Fact]
    public void Virtual_cron_conversation_shows_badge_and_hides_archive_button()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "General", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";
        var conv = _store.GetAgent("a-1")!.Conversations["c-1"];
        conv.IsVirtualSession = true;
        conv.VirtualSessionKind = "cron";

        var cut = RenderLayout();

        Assert.Contains("Cron", cut.Markup);
        Assert.Empty(cut.FindAll(".conversation-archive-btn"));
    }

    [Fact]
    public void Switching_agent_triggers_history_load_for_active_conversation()
    {
        // Arrange: two agents, each with a default conversation auto-selected via SeedConversations
        _store.SeedAgents([
            new AgentSummary("a-1", "Alpha"),
            new AgentSummary("a-2", "Beta")
        ]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Default", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SeedConversations("a-2", [
            new ConversationSummaryDto("c-2", "a-2", "Default", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        // Act: switch to agent a-2 via dropdown
        var dropdown = cut.Find(".agent-dropdown-select");
        dropdown.Change("a-2");

        // Assert: SelectConversationAsync was called for Beta's auto-selected conversation
        _interaction.Received(1).SelectConversationAsync("a-2", "c-2");
    }
}
