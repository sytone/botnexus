using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
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
    private readonly StubToolsHandler _toolsHandler;
    private readonly StubNavOrderHandler _navOrderHandler;

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
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
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
        _ctx.Services.AddSingleton(new CronApiClient(http));
        _ctx.Services.AddSingleton(new SectionsApiClient(http));
        _toolsHandler = new StubToolsHandler();
        var toolsHttp = new HttpClient(_toolsHandler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(new ToolsApiClient(toolsHttp));
        _navOrderHandler = new StubNavOrderHandler();
        var navOrderHttp = new HttpClient(_navOrderHandler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(new NavOrderApiClient(navOrderHttp));
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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        var activeConv = cut.Find(".conversation-list-item-btn.active");
        Assert.NotNull(activeConv);
    }

    /// <summary>
    /// #2305 (epic #2300): a cron conversation is badged "Cron" and offers CLOSE (not archive)
    /// purely because the SERVER stamped <c>source="Cron"</c>. No mutable flag, no id prefix.
    /// </summary>
    [Fact]
    public async Task Cron_source_conversation_shows_badge_and_close_button()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "General", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", "Cron")
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
        Assert.Equal(ConversationSource.Cron, _store.GetAgent("a-1")!.Conversations["c-1"].Source);

        var cut = RenderLayout();

        // Cron conversations are now in a collapsed Scheduled group; expand it first
        await cut.InvokeAsync(() => cut.Find("[data-testid='cron-group-toggle']").Click());

        Assert.Contains("Cron", cut.Markup);
        var archiveBtn = cut.Find(".conversation-archive-btn");
        Assert.Contains("✕", archiveBtn.TextContent);
        Assert.Contains("Close conversation", archiveBtn.GetAttribute("title"));
    }

    /// <summary>
    /// #2340: hiding runtime-internal threads reads the first-class, server-stamped
    /// <c>ConversationVisibility</c>. The <c>internal:</c> id-prefix probe this replaced is gone and
    /// fenced; note the id here is deliberately a plain one, proving the hiding decision no longer
    /// depends on the id text at all.
    /// </summary>
    [Fact]
    public void Internal_hidden_conversation_is_hidden_from_user_conversation_list()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "General", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-2", "a-1", "Internal sub-agent", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", "Channel", "InternalHidden")
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        Assert.Contains("General", cut.Markup);
        Assert.DoesNotContain("Internal sub-agent", cut.Markup);
    }

    /// <summary>
    /// #2340 regression guard in the opposite direction: an <c>internal:</c>-shaped id with the
    /// back-compat default visibility must now STAY visible. Under the deleted prefix probe this row
    /// vanished purely because of how its id happened to be spelled - the silent failure mode that
    /// motivated replacing the probe with a typed field.
    /// </summary>
    [Fact]
    public void Internal_prefixed_id_with_default_visibility_is_still_listed()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "General", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("internal:sub-1", "a-1", "Legit id-shaped thread", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        Assert.Contains("General", cut.Markup);
        Assert.Contains("Legit id-shaped thread", cut.Markup);
    }

    /// <summary>
    /// #2340: <c>InspectableReadOnly</c> is visible-but-not-writable, so it must still be LISTED.
    /// Only <c>InternalHidden</c> removes a row; write gating is the independent job of
    /// <c>ConversationRenderProjection</c>. Collapsing the two would silently hide audit views.
    /// </summary>
    [Fact]
    public void Inspectable_read_only_conversation_is_still_listed()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Observer view", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", "Channel", "InspectableReadOnly")
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        Assert.Contains("Observer view", cut.Markup);
    }

    [Fact]
    public void Internal_hidden_conversations_are_not_rendered_as_selectable_conversation_rows()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "General", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-2", "a-1", "Internal routing thread", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", "Channel", "InternalHidden")
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
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
        // A genuine sub-agent observer entry (IMMUTABLE IsObserverAgent kind) can only become the
        // active view via the explicit SubAgentView source (the store's anti-hijack guard rejects
        // every other source onto a read-only agent). #2248: read-only is keyed on the immutable
        // kind, never the mutable SessionType.
        _store.UpsertAgent(new AgentState
        {
            AgentId = "sub-1",
            DisplayName = "Subagent",
            SessionType = "agent-subagent",
            IsObserverAgent = true,
            IsConnected = true
        });
        _store.SeedConversations("sub-1", []);
        _store.SelectView("sub-1", string.Empty, SelectionSource.SubAgentView);

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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        Assert.Empty(cut.FindAll(".conversation-archive-btn"));
    }

    [Fact]
    public void Sidebar_scroll_region_exists_within_nav()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        // Scroll region should be present inside the sidebar nav
        cut.Find(".sidebar-scroll-region");
    }

    [Fact]
    public void Configuration_and_agents_links_are_outside_scroll_region()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

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
            Assert.EndsWith("/agent/a-1/conversation/c-1", nav.Uri));
    }

    [Fact]
    public void In_app_conversation_selection_updates_url_with_conversation_route()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "First", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-2", "a-1", "Second", false, "Active", null, 0, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(1))
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/agent/a-1/conversation/c-1");

        var cut = RenderLayout();
        // Wait for async renders to stabilize before clicking
        cut.WaitForState(() => cut.FindAll(".conversation-list-item-btn").Count >= 2);
        cut.InvokeAsync(() => cut.FindAll(".conversation-list-item-btn")
            .First(btn => btn.TextContent.Contains("Second", StringComparison.Ordinal))
            .Click());

        cut.WaitForAssertion(() =>
            Assert.EndsWith("/agent/a-1/conversation/c-2", nav.Uri));
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

        var expectedSuffix = $"/agent/{Uri.EscapeDataString(agentId)}/conversation/{Uri.EscapeDataString(conversationId)}";
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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

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
        // A real agent and a sub-agent (read-only via the IMMUTABLE IsObserverAgent kind) are both in the store
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.UpsertAgent(new AgentState
        {
            AgentId = "sub-xyz",
            DisplayName = "SubTask",
            SessionType = "agent-subagent",
            IsObserverAgent = true,
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
            IsObserverAgent = true,
            IsConnected = true
        });

        var cut = RenderLayout();

        Assert.Empty(cut.FindAll(".agent-dropdown-select"));
    }

    [Fact]
    public async Task SubAgentSpawned_style_session_poisoning_keeps_user_agent_in_roster_and_selection_unchanged()
    {
        // #2248 regression: a real user-agent conversation is active. An inbound sub-agent session
        // event (RegisterSession agent-subagent, exactly what HandleSubAgentSpawned drives) lands and
        // stamps the user agent's mutable SessionType. The user agent must STAY in the dropdown
        // roster and the active-view selection must NOT revert.
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "General", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("a-1", "c-1", SelectionSource.UserClick);

        var cut = RenderLayout();
        var agentBefore = _store.ActiveAgentId;
        var convBefore = _store.ActiveConversationId;

        // Simulate the inbound SubAgentSpawned data churn: poison the user agent's SessionType.
        await cut.InvokeAsync(() =>
        {
            _store.RegisterSession("a-1", "sess-poison", sessionType: "agent-subagent");
            _store.NotifyChanged();
        });

        var options = cut.FindAll(".agent-dropdown-select option");
        Assert.Contains(options, o => o.GetAttribute("value") == "a-1");
        _store.ActiveAgentId.ShouldBe(agentBefore,
            customMessage: "An inbound sub-agent session event must not revert the active agent (#2248).");
        _store.ActiveConversationId.ShouldBe(convBefore,
            customMessage: "An inbound sub-agent session event must not revert the active conversation (#2248).");
    }

    [Fact]
    public void AgentDropdown_Rendered_EvenWhenIsMobileIsTrue()
    {
        // Desktop MainLayout always renders agent dropdown regardless of viewport width.
        // Narrow viewport on desktop still uses MainLayout (not MobileLayout), so the
        // agent list must remain visible.
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.NotifyChanged();

        // Simulate narrow viewport: chatScroll.isMobileView returns true
        _ctx.JSInterop.Setup<bool>("chatScroll.isMobileView").SetResult(true);

        var cut = RenderLayout();

        // Agent dropdown must still be present
        Assert.NotEmpty(cut.FindAll("[data-testid='agent-select']"));
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
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        // The conversation list item button must be rendered as an <a> tag
        var anchor = cut.Find(".conversation-list-item-btn");
        Assert.Equal("a", anchor.TagName.ToLowerInvariant());
    }

    [Fact]
    public void Conversation_list_items_have_correct_href()
    {
        // #699 + #2247: the href must point to the canonical route-owned
        // /agent/{agentId}/conversation/{conversationId} path so the browser can open the
        // conversation directly via right-click, and refresh/back restore exactly that view.
        const string agentId = "a-1";
        const string convId = "c-1";
        _store.SeedAgents([new AgentSummary(agentId, "Alpha")]);
        _store.SeedConversations(agentId, [
            new ConversationSummaryDto(convId, agentId, "My Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView(agentId, string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        var anchor = cut.Find(".conversation-list-item-btn");
        var href = anchor.GetAttribute("href");
        Assert.NotNull(href);
        Assert.Contains($"agent/{Uri.EscapeDataString(agentId)}/conversation/{Uri.EscapeDataString(convId)}", href);
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

    [Fact]
    public void Agent_dropdown_visible_even_when_viewport_is_narrow()
    {
        // Simulate narrow viewport: chatScroll.isMobileView returns true
        _ctx.JSInterop.Setup<bool>("chatScroll.isMobileView").SetResult(true);

        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "General", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        // The agent dropdown must still be rendered in MainLayout even on narrow viewports
        // because desktop users resize their browser but stay on MainLayout (not MobileLayout)
        var select = cut.Find("[data-testid='agent-select']");
        Assert.NotNull(select);
    }

    // ── Conversation activity filter (None / Today / This Week) ──────────────────────────────

    [Fact]
    public void Conversation_filter_bar_renders_three_buttons_when_agent_active()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Chat 1", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        cut.Find("[data-testid='conversation-filter-bar']");
        Assert.Equal("None", cut.Find("[data-testid='conversation-filter-none']").TextContent.Trim());
        Assert.Equal("Today", cut.Find("[data-testid='conversation-filter-today']").TextContent.Trim());
        Assert.Equal("This Week", cut.Find("[data-testid='conversation-filter-week']").TextContent.Trim());
    }

    [Fact]
    public void Conversation_filter_replaces_redundant_conversations_heading()
    {
        // The redundant inner "Conversations" group label is replaced by the filter bar.
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Chat 1", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        // The conversations group no longer renders a label element inside its header.
        var group = cut.Find("[data-testid='conversation-group-conversations']");
        Assert.DoesNotContain("conversation-group-label", group.InnerHtml);
    }

    [Fact]
    public void Conversation_filter_defaults_to_none_and_shows_all()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-recent", "a-1", "Recent Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-old", "a-1", "Old Chat", false, "Active", null, 0, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-30))
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        // None is active by default and both conversations are visible.
        Assert.Contains("active", cut.Find("[data-testid='conversation-filter-none']").GetAttribute("class"));
        Assert.Contains("Recent Chat", cut.Markup);
        Assert.Contains("Old Chat", cut.Markup);
    }

    [Fact]
    public async Task Conversation_filter_today_hides_conversations_updated_before_today()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-today", "a-1", "Today Chat", false, "Active", null, 0, DateTimeOffset.Now, DateTimeOffset.Now),
            new ConversationSummaryDto("c-yesterday", "a-1", "Yesterday Chat", false, "Active", null, 0, DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddDays(-2))
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();
        await cut.InvokeAsync(() => cut.Find("[data-testid='conversation-filter-today']").Click());

        Assert.Contains("active", cut.Find("[data-testid='conversation-filter-today']").GetAttribute("class"));
        Assert.Contains("Today Chat", cut.Markup);
        Assert.DoesNotContain("Yesterday Chat", cut.Markup);
    }

    [Fact]
    public async Task Conversation_filter_this_week_hides_conversations_older_than_seven_days()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-recent", "a-1", "Recent Chat", false, "Active", null, 0, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-3)),
            new ConversationSummaryDto("c-old", "a-1", "Old Chat", false, "Active", null, 0, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-30))
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();
        await cut.InvokeAsync(() => cut.Find("[data-testid='conversation-filter-week']").Click());

        Assert.Contains("active", cut.Find("[data-testid='conversation-filter-week']").GetAttribute("class"));
        Assert.Contains("Recent Chat", cut.Markup);
        Assert.DoesNotContain("Old Chat", cut.Markup);
    }

    [Fact]
    public async Task Conversation_filter_today_with_no_matches_shows_empty_range_message()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-old", "a-1", "Old Chat", false, "Active", null, 0, DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now.AddDays(-30))
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();
        await cut.InvokeAsync(() => cut.Find("[data-testid='conversation-filter-today']").Click());

        cut.Find("[data-testid='conversation-filter-empty']");
        Assert.DoesNotContain("Old Chat", cut.Markup);
    }

    [Fact]
    public async Task Conversation_filter_today_does_not_hide_pinned_or_scheduled_groups()
    {
        // Pinned and scheduled groups carry their own intent and must remain visible
        // regardless of the activity filter, even when their items are old.
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-normal-old", "a-1", "Normal Old", false, "Active", null, 0, DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now.AddDays(-30)),
            new ConversationSummaryDto("c-pinned-old", "a-1", "Pinned Old", false, "Active", null, 0, DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now.AddDays(-30))
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
        _store.GetAgent("a-1")!.Conversations["c-pinned-old"].IsPinned = true;

        var cut = RenderLayout();
        await cut.InvokeAsync(() => cut.Find("[data-testid='conversation-filter-today']").Click());

        // Pinned group is unaffected by the filter.
        cut.Find("[data-testid='conversation-group-pinned']");
        Assert.Contains("Pinned Old", cut.Markup);
        // The old normal conversation is filtered out.
        Assert.DoesNotContain("Normal Old", cut.Markup);
    }

    [Fact]
    public void Conversation_filter_restores_persisted_selection_on_init()
    {
        // A previously-chosen filter persisted in localStorage is applied on first render.
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "botnexus-conversation-activity-filter")
            .SetResult("ThisWeek");
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-recent", "a-1", "Recent Chat", false, "Active", null, 0, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-3)),
            new ConversationSummaryDto("c-old", "a-1", "Old Chat", false, "Active", null, 0, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-30))
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();

        Assert.Contains("active", cut.Find("[data-testid='conversation-filter-week']").GetAttribute("class"));
        Assert.Contains("Recent Chat", cut.Markup);
        Assert.DoesNotContain("Old Chat", cut.Markup);
    }

    [Fact]
    public async Task Conversation_filter_click_persists_selection_to_local_storage()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Chat 1", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();
        await cut.InvokeAsync(() => cut.Find("[data-testid='conversation-filter-today']").Click());

        _ctx.JSInterop.VerifyInvoke("localStorage.setItem");
        var setItemCall = _ctx.JSInterop.Invocations["localStorage.setItem"]
            .Last(i => i.Arguments.Count == 2 && (string?)i.Arguments[0] == "botnexus-conversation-activity-filter");
        Assert.Equal("Today", setItemCall.Arguments[1]);
    }

    // ── Tools nav section (#2233, slice 2 of #2231) ─────────────────────────────────────────

    [Fact]
    public void Tools_section_header_is_rendered()
    {
        var cut = RenderLayout();
        var link = cut.Find("[data-testid='nav-tools']");
        Assert.Contains("Tools", link.TextContent);
        Assert.Equal("tools", link.GetAttribute("href"));
    }

    [Fact]
    public void Tools_section_renders_above_chat()
    {
        var cut = RenderLayout();

        var markup = cut.Markup;
        var toolsIndex = markup.IndexOf("data-testid=\"nav-tools\"", StringComparison.Ordinal);
        var chatIndex = markup.IndexOf("href=\"chat\"", StringComparison.Ordinal);
        Assert.True(toolsIndex >= 0 && chatIndex >= 0);
        Assert.True(toolsIndex < chatIndex, "Tools section must render above the Chat link.");
    }

    [Fact]
    public void Tools_section_shows_empty_state_when_no_tools()
    {
        _toolsHandler.SetTools("[]");

        var cut = RenderLayout();

        cut.WaitForAssertion(() => cut.Find("[data-testid='tools-empty']"));
        Assert.Contains("No tools configured", cut.Find("[data-testid='tools-empty']").TextContent);
    }

    [Fact]
    public void Tools_section_renders_configured_tools_from_fake_source()
    {
        _toolsHandler.SetTools("""
            [
              { "id": "t-1", "name": "Grafana", "url": "https://grafana", "icon": "\uD83D\uDCC8", "order": 0 },
              { "id": "t-2", "name": "Wiki", "url": "https://wiki", "icon": "", "order": 1 }
            ]
            """);

        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
            Assert.Equal(2, cut.FindAll("[data-testid='tools-subnav-item']").Count));
        Assert.Contains("Grafana", cut.Markup);
        Assert.Contains("Wiki", cut.Markup);
    }

    [Fact]
    public void Tools_sub_items_link_to_tool_route()
    {
        _toolsHandler.SetTools("""
            [ { "id": "t-1", "name": "Grafana", "url": "https://grafana", "icon": "", "order": 0 } ]
            """);

        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
        {
            var item = cut.Find("[data-testid='tools-subnav-item']");
            Assert.Equal("tools/t-1", item.GetAttribute("href"));
        });
    }

    [Fact]
    public void Tools_sub_items_render_in_ascending_order()
    {
        _toolsHandler.SetTools("""
            [
              { "id": "t-b", "name": "Beta", "url": "https://b", "icon": "", "order": 5 },
              { "id": "t-a", "name": "Alpha", "url": "https://a", "icon": "", "order": 1 }
            ]
            """);

        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll("[data-testid='tools-subnav-item']");
            Assert.Equal(2, items.Count);
            Assert.Contains("Alpha", items[0].TextContent);
            Assert.Contains("Beta", items[1].TextContent);
        });
    }

    // -- Sidebar ordering model (#2236, slice 5 of #2231) -----------------------------------

    [Fact]
    public void Nav_renders_all_builtin_items_in_default_order()
    {
        var cut = RenderLayout();

        var markup = cut.Markup;
        int Idx(string needle) => markup.IndexOf(needle, StringComparison.Ordinal);

        var activity = Idx("Activity");
        var tools = Idx("data-testid=\"nav-tools\"");
        var chat = Idx("href=\"chat\"");
        var config = Idx("href=\"configuration\"");
        var agents = Idx("href=\"agents\"");
        var cron = Idx("data-testid=\"nav-cron-jobs\"");

        Assert.True(activity >= 0 && tools >= 0 && chat >= 0 && config >= 0 && agents >= 0 && cron >= 0);
        Assert.True(activity < tools, "Activity must precede Tools by default.");
        Assert.True(tools < chat, "Tools must precede Chat by default.");
        Assert.True(chat < config, "Chat must precede Configuration by default.");
        Assert.True(config < agents, "Configuration must precede Agents by default.");
        Assert.True(agents < cron, "Agents must precede Cron by default.");
    }

    [Fact]
    public void Nav_default_order_places_tools_above_chat()
    {
        _navOrderHandler.SetOrder("[]");

        var cut = RenderLayout();

        var markup = cut.Markup;
        var toolsIndex = markup.IndexOf("data-testid=\"nav-tools\"", StringComparison.Ordinal);
        var chatIndex = markup.IndexOf("href=\"chat\"", StringComparison.Ordinal);
        Assert.True(toolsIndex >= 0 && chatIndex >= 0);
        Assert.True(toolsIndex < chatIndex, "Tools must render above Chat by default.");
    }

    [Fact]
    public void Lowering_tools_order_moves_it_above_activity()
    {
        _navOrderHandler.SetOrder("""
            [
              { "key": "tools", "order": 5 },
              { "key": "activity", "order": 10 },
              { "key": "chat", "order": 30 },
              { "key": "configuration", "order": 40 },
              { "key": "skills", "order": 50 },
              { "key": "agents", "order": 60 },
              { "key": "cron", "order": 70 }
            ]
            """);

        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            var tools = markup.IndexOf("data-testid=\"nav-tools\"", StringComparison.Ordinal);
            var activity = markup.IndexOf("Activity", StringComparison.Ordinal);
            var chat = markup.IndexOf("href=\"chat\"", StringComparison.Ordinal);
            Assert.True(tools >= 0 && activity >= 0 && chat >= 0);
            Assert.True(tools < activity, "Lowering Tools order must move it above Activity.");
            Assert.True(tools < chat, "Tools must remain above Chat.");
        });
    }

    [Fact]
    public void Custom_order_can_move_chat_to_top()
    {
        _navOrderHandler.SetOrder("""
            [
              { "key": "chat", "order": 1 },
              { "key": "activity", "order": 10 },
              { "key": "tools", "order": 20 },
              { "key": "configuration", "order": 40 },
              { "key": "skills", "order": 50 },
              { "key": "agents", "order": 60 },
              { "key": "cron", "order": 70 }
            ]
            """);

        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            var chat = markup.IndexOf("href=\"chat\"", StringComparison.Ordinal);
            var activity = markup.IndexOf("Activity", StringComparison.Ordinal);
            var tools = markup.IndexOf("data-testid=\"nav-tools\"", StringComparison.Ordinal);
            Assert.True(chat >= 0 && activity >= 0 && tools >= 0);
            Assert.True(chat < activity, "Chat override to order 1 must render above Activity.");
            Assert.True(chat < tools, "Chat override to order 1 must render above Tools.");
        });
    }

    [Fact]
    public void Malformed_nav_order_falls_back_to_builtin_default_order()
    {
        _navOrderHandler.SetRaw("not json");

        var cut = RenderLayout();

        var markup = cut.Markup;
        var tools = markup.IndexOf("data-testid=\"nav-tools\"", StringComparison.Ordinal);
        var chat = markup.IndexOf("href=\"chat\"", StringComparison.Ordinal);
        Assert.True(tools >= 0 && chat >= 0);
        Assert.True(tools < chat, "Fallback default order must keep Tools above Chat.");
    }

    /// <summary>
    /// Controllable fake nav-order source. Returns the JSON body configured via
    /// <see cref="SetOrder"/> for GET /api/nav-order, defaulting to the built-in effective order.
    /// </summary>
    private sealed class StubNavOrderHandler : HttpMessageHandler
    {
        private string _json = """
            [
              { "key": "activity", "order": 10 },
              { "key": "tools", "order": 20 },
              { "key": "chat", "order": 30 },
              { "key": "configuration", "order": 40 },
              { "key": "skills", "order": 50 },
              { "key": "agents", "order": 60 },
              { "key": "cron", "order": 70 }
            ]
            """;

        public void SetOrder(string json) => _json = json;

        public void SetRaw(string raw) => _json = raw;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Controllable fake tools source. Returns the JSON body configured via <see cref="SetTools"/>
    /// for GET /api/tools, defaulting to an empty list so the nav renders its empty state.
    /// </summary>
    private sealed class StubToolsHandler : HttpMessageHandler
    {
        private string _toolsJson = "[]";

        public void SetTools(string json) => _toolsJson = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_toolsJson, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
