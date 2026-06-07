using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
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

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(hub);
        _ctx.Services.AddSingleton(gatewayInfo);
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        var mockPrefs = Substitute.For<IPortalPreferencesService>();
        mockPrefs.Current.Returns(new PortalPreferences());
        _ctx.Services.AddSingleton(mockPrefs);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
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
    public async Task Clicking_burger_opens_sidebar()
    {
        var cut = RenderLayout();

        await cut.InvokeAsync(() => cut.Find(".burger-btn").Click());

        cut.Find(".sidebar-open");
    }

    [Fact]
    public async Task Clicking_burger_twice_closes_sidebar()
    {
        var cut = RenderLayout();

        await cut.InvokeAsync(() => cut.Find(".burger-btn").Click());
        await cut.InvokeAsync(() => cut.Find(".burger-btn").Click());

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
    public void Shows_agent_emoji_prefix_in_dropdown_when_available()
    {
        _store.SeedAgents([
            new AgentSummary("a-1", "Farnsworth", "🔬"),
            new AgentSummary("a-2", "UnnamedAgent")
        ]);
        _store.NotifyChanged();

        var cut = RenderLayout();
        var options = cut.FindAll(".agent-dropdown-select option");

        Assert.Contains(options, option => option.TextContent.Trim() == "🔬 Farnsworth");
        Assert.Contains(options, option => option.TextContent.Trim() == "UnnamedAgent");
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
    public async Task Virtual_cron_conversation_shows_badge_and_close_button()
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

        // Cron conversations are now in a collapsed Scheduled group; expand it first
        await cut.InvokeAsync(() => cut.Find("[data-testid='cron-group-toggle']").Click());

        Assert.Contains("Cron", cut.Markup);
        var archiveBtn = cut.Find(".conversation-archive-btn");
        Assert.Contains("✕", archiveBtn.TextContent);
        Assert.Contains("Close conversation", archiveBtn.GetAttribute("title"));
    }

    [Fact]
    public void Virtual_internal_conversation_is_hidden_from_user_conversation_list()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "General", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-2", "a-1", "Internal sub-agent", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";

        var internalConversation = _store.GetAgent("a-1")!.Conversations["c-2"];
        internalConversation.IsVirtualSession = true;
        internalConversation.VirtualSessionKind = "internal";

        var cut = RenderLayout();

        Assert.Contains("General", cut.Markup);
        Assert.DoesNotContain("Internal sub-agent", cut.Markup);
    }

    [Fact]
    public void Internal_prefix_conversation_is_hidden_from_user_conversation_list()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "General", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("internal:sub-1", "a-1", "Internal routing thread", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        Assert.Contains("General", cut.Markup);
        Assert.DoesNotContain("Internal routing thread", cut.Markup);
    }

    [Fact]
    public void Internal_conversations_are_not_rendered_as_selectable_conversation_rows()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "General", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("internal:sub-1", "a-1", "Internal routing thread", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        Assert.Single(cut.FindAll(".conversation-list-item-btn"));
    }

    [Fact]
    public async Task Clicking_sub_agent_row_routes_to_read_only_sub_agent_view()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "General", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";
        _store.GetAgent("a-1")!.SubAgents["sub-1"] = new SubAgentInfo
        {
            SubAgentId = "sub-1",
            Name = "Scout",
            Task = "Inspect repository",
            Status = "Running",
            StartedAt = DateTimeOffset.UtcNow
        };

        var cut = RenderLayout();
        // WaitForState stabilises the first render, then await InvokeAsync so any subsequent
        // async re-renders (e.g. isMobileView JS interop in OnAfterRenderAsync) complete and
        // event handler IDs are stable before we assert.
        cut.WaitForState(() => cut.FindAll(".agent-session-item").Count > 0);
        await cut.InvokeAsync(() => cut.Find(".agent-session-item").Click());

        await _interaction.Received(1).ViewSubAgentAsync(
            Arg.Is<SubAgentInfo>(s => s.SubAgentId == "sub-1"));
    }

    [Fact]
    public void Read_only_agent_hides_new_conversation_button()
    {
        _store.SeedAgents([new AgentSummary("sub-1", "Subagent")]);
        _store.SeedConversations("sub-1", []);
        _store.ActiveAgentId = "sub-1";
        _store.GetAgent("sub-1")!.SessionType = "agent-subagent";

        var cut = RenderLayout();

        Assert.Empty(cut.FindAll(".conversation-new-btn"));
    }

    [Fact]
    public void Non_default_conversation_shows_archive_button()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "My Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        var archiveBtn = cut.Find(".conversation-archive-btn");
        Assert.Contains("🗑️", archiveBtn.TextContent);
        Assert.Contains("Archive conversation", archiveBtn.GetAttribute("title"));
    }

    [Fact]
    public void Default_conversation_hides_archive_button()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Default", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        Assert.Empty(cut.FindAll(".conversation-archive-btn"));
    }

    [Fact]
    public void Sidebar_scroll_region_exists_within_nav()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        // Scroll region should be present inside the sidebar nav
        cut.Find(".sidebar-scroll-region");
    }

    [Fact]
    public void Configuration_and_agents_links_are_outside_scroll_region()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        // The configuration and agents links should be siblings of the scroll region, not children
        var scrollRegion = cut.Find(".sidebar-scroll-region");
        Assert.DoesNotContain("Configuration", scrollRegion.TextContent);
        Assert.DoesNotContain("Agents", scrollRegion.TextContent);

        // But they should exist in the sidebar nav
        var nav = cut.Find(".sidebar-nav");
        Assert.Contains("Configuration", nav.TextContent);
        Assert.Contains("Agents", nav.TextContent);
    }

    [Fact]
    public void Conversation_list_scroll_container_wraps_conversation_rows()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Chat 1", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        var scrollContainer = cut.Find(".conversation-list-scroll");
        Assert.Single(scrollContainer.QuerySelectorAll(".conversation-list-item-btn"));
    }

    [Fact]
    public void Conversation_list_scroll_container_handles_many_conversations()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations(
            "a-1",
            Enumerable.Range(1, 40)
                .Select(i => new ConversationSummaryDto(
                    $"c-{i}",
                    "a-1",
                    $"Chat {i}",
                    false,
                    "Active",
                    null,
                    0,
                    DateTimeOffset.UtcNow.AddMinutes(-i),
                    DateTimeOffset.UtcNow.AddMinutes(-i)))
                .ToList());
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        var scrollContainer = cut.Find(".conversation-list-scroll");
        Assert.Equal(40, scrollContainer.QuerySelectorAll(".conversation-list-item-btn").Length);
    }

    [Fact]
    public void Direct_route_to_chat_marks_chat_section_active()
    {
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/chat");

        var cut = RenderLayout();

        var chatLink = cut.Find("a[href='chat']");
        Assert.Contains("active", chatLink.ClassName);
    }

    [Fact]
    public void Hard_refresh_on_configuration_path_marks_configuration_section_active()
    {
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/configuration");

        var cut = RenderLayout();

        var configLink = cut.Find("a[href='configuration']");
        Assert.Contains("active", configLink.ClassName);
    }

    [Fact]
    public void In_app_agent_selection_updates_url_with_agent_route()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Default", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/chat");

        var cut = RenderLayout();
        cut.Find(".agent-dropdown-select").Change("a-1");

        cut.WaitForAssertion(() =>
            Assert.EndsWith("/chat/a-1/c-1", nav.Uri));
    }

    [Fact]
    public void In_app_conversation_selection_updates_url_with_conversation_route()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "First", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-2", "a-1", "Second", false, "Active", null, 0, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(1))
        ]);
        _store.ActiveAgentId = "a-1";

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/chat/a-1/c-1");

        var cut = RenderLayout();
        // Wait for async renders to stabilize before clicking
        cut.WaitForState(() => cut.FindAll(".conversation-list-item-btn").Count >= 2);
        cut.InvokeAsync(() => cut.FindAll(".conversation-list-item-btn")
            .First(btn => btn.TextContent.Contains("Second", StringComparison.Ordinal))
            .Click());

        cut.WaitForAssertion(() =>
            Assert.EndsWith("/chat/a-1/c-2", nav.Uri));
    }

    [Fact]
    public void In_app_selection_url_encodes_agent_and_conversation_ids()
    {
        const string agentId = "agent/x";
        const string conversationId = "conv/1 with space";
        _store.SeedAgents([new AgentSummary(agentId, "Encoded Agent")]);
        _store.SeedConversations(agentId, [
            new ConversationSummaryDto(conversationId, agentId, "Encoded Conversation", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/chat");

        var cut = RenderLayout();
        cut.Find(".agent-dropdown-select").Change(agentId);

        var expectedSuffix = $"/chat/{Uri.EscapeDataString(agentId)}/{Uri.EscapeDataString(conversationId)}";
        cut.WaitForAssertion(() =>
            Assert.EndsWith(expectedSuffix, nav.Uri));
    }

    [Fact]
    public async Task Switching_agent_triggers_history_load_for_active_conversation()
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

        // Act: switch to agent a-2 via dropdown.
        // InvokeAsync ensures the bUnit renderer flushes the full async OnAgentSelected
        // pipeline (including the await inside) before we assert. This is required because
        // GlobalErrorBoundary (now wrapping @Body) uses ErrorBoundaryBase, which changes
        // how bUnit dispatches async component updates.
        var dropdown = cut.Find(".agent-dropdown-select");
        await cut.InvokeAsync(() => dropdown.Change("a-2"));

        // Assert: SelectConversationAsync was called for Beta's auto-selected conversation.
        // OnAgentSelected is async -- wrap in WaitForAssertion so bUnit waits for the async
        // event handler to complete before asserting. Without this, the assertion can race
        // the async continuation on slow CI runners and report a false negative (#828).
        cut.WaitForAssertion(() => _interaction.Received(1).SelectConversationAsync("a-2", "c-2"));
    }

    [Fact]
    public void Sub_agents_in_store_are_not_shown_in_top_level_agent_dropdown()
    {
        // A real agent and a sub-agent (IsReadOnly via SessionType=agent-subagent) are both in the store
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.UpsertAgent(new AgentState
        {
            AgentId = "sub-xyz",
            DisplayName = "SubTask",
            SessionType = "agent-subagent",
            IsConnected = true
        });

        var cut = RenderLayout();

        var options = cut.FindAll(".agent-dropdown-select option");
        Assert.Contains(options, o => o.GetAttribute("value") == "a-1");
        Assert.DoesNotContain(options, o => o.GetAttribute("value") == "sub-xyz");
    }

    [Fact]
    public void Sub_agent_only_store_renders_no_agent_dropdown()
    {
        // If the only entries are sub-agents the dropdown should not appear at all
        _store.UpsertAgent(new AgentState
        {
            AgentId = "sub-xyz",
            DisplayName = "SubTask",
            SessionType = "agent-subagent",
            IsConnected = true
        });

        var cut = RenderLayout();

        Assert.Empty(cut.FindAll(".agent-dropdown-select"));
    }

    [Fact]
    public void AgentDropdown_NotRendered_WhenIsMobileIsTrue()
    {
        // Arrange: seed an agent so the dropdown would normally appear
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.NotifyChanged();

        // Simulate JS isMobileView returning true (fires in OnAfterRenderAsync)
        _ctx.JSInterop.Setup<bool>("chatScroll.isMobileView").SetResult(true);

        // Act
        var cut = RenderLayout();
        // Wait for after-render to fire (_isMobile to be set)
        cut.WaitForAssertion(() =>
            Assert.Empty(cut.FindAll(".agent-dropdown-select")),
            TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void AgentDropdown_Rendered_WhenIsMobileIsFalse()
    {
        // Desktop default: isMobileView returns false (default Loose mock behavior)
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.NotifyChanged();

        _ctx.JSInterop.Setup<bool>("chatScroll.isMobileView").SetResult(false);

        var cut = RenderLayout();

        // Dropdown should be visible on desktop
        cut.Find(".agent-dropdown-select");
    }

    [Fact]
    public void Conversation_list_items_render_as_anchor_elements()
    {
        // #699: conversation items must be <a> elements so the browser exposes
        // "Open in new tab" on right-click and supports Ctrl+click / middle-click.
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "My Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        // The conversation list item button must be rendered as an <a> tag
        var anchor = cut.Find(".conversation-list-item-btn");
        Assert.Equal("a", anchor.TagName.ToLowerInvariant());
    }

    [Fact]
    public void Conversation_list_items_have_correct_href()
    {
        // #699: the href must point to the routable /chat/{agentId}/{conversationId} path
        // so the browser can open the conversation directly via right-click.
        const string agentId = "a-1";
        const string convId = "c-1";
        _store.SeedAgents([new AgentSummary(agentId, "Alpha")]);
        _store.SeedConversations(agentId, [
            new ConversationSummaryDto(convId, agentId, "My Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = agentId;

        var cut = RenderLayout();

        var anchor = cut.Find(".conversation-list-item-btn");
        var href = anchor.GetAttribute("href");
        Assert.NotNull(href);
        Assert.Contains($"chat/{Uri.EscapeDataString(agentId)}/{Uri.EscapeDataString(convId)}", href);
    }

    [Fact]
    public void Restart_Gateway_button_is_not_rendered()
    {
        // #794: the Restart Gateway button was removed because it killed the gateway
        // with no automatic recovery -- no process supervisor is present.
        var cut = RenderLayout();

        Assert.Empty(cut.FindAll(".restart-btn"));
        Assert.DoesNotContain("Restart Gateway", cut.Markup);
    }

    [Fact]
    public void Sidebar_footer_is_still_rendered_without_restart_button()
    {
        // The sidebar footer (build info, update badge) must survive the button removal.
        var cut = RenderLayout();

        cut.Find(".sidebar-footer");
    }
}
