using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class FocusedSidebarTests : IDisposable
{
    private readonly IPortalPreferencesService _prefs;
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;
    private readonly SidebarHandler _handler = new();

    public FocusedSidebarTests()
    {
        _store = new ClientStateStore();
        var interaction = Substitute.For<IAgentInteractionService>();
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);

        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(new GatewayHubConnection());
        _ctx.Services.AddSingleton(new GatewayInfoService(http, restClient));
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        _prefs = Substitute.For<IPortalPreferencesService>();
        var prefs = _prefs;
        prefs.Current.Returns(new PortalPreferences());
        _ctx.Services.AddSingleton(prefs);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
        _ctx.Services.AddSingleton(new CronApiClient(http));
        _ctx.Services.AddSingleton(new SectionsApiClient(http));
        _ctx.Services.AddSingleton(sp => new ConversationSectionsState(sp.GetRequiredService<SectionsApiClient>()));
        _ctx.Services.AddSingleton(new ToolsApiClient(http));
        _ctx.Services.AddStubNavOrderApiClient();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<MainLayout> RenderLayout() =>
        _ctx.Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(_ => { })));

    private void Seed(params ConversationSummaryDto[] conversations)
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", conversations);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
    }

    // Seeds a persisted collapse flag so the rendered output is deterministic. Under the Loose JS
    // mock an unseeded localStorage read returns null, and MainLayout.OnAfterRenderAsync applies
    // that on an async continuation which can race a toggle click (the same flake the Scheduled
    // group tests already guard against with an explicit seed).
    private void SeedCollapsed(string key, bool collapsed) =>
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", key).SetResult(collapsed ? "true" : "false");

    private static ConversationSummaryDto Conv(string id, string title, string source = "Channel", bool pinned = false) =>
        new(id, "a-1", title, false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", source, IsPinned: pinned);


    private sealed class SidebarHandler : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        public string AgentsJson { get; set; } = "[{\"agentId\":\"a-1\",\"displayName\":\"Alpha\"}]";
        public string NavOrderJson { get; set; } = "[]";
        public string ToolsJson { get; set; } = "[]";
        public string SectionsJson { get; set; } = "{\"sections\":[],\"assignments\":{}}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method.Method);
            var json = request.RequestUri?.AbsolutePath switch
            {
                "/api/agents" => AgentsJson,
                "/api/nav-order" => NavOrderJson,
                "/api/tools" => ToolsJson,
                "/api/agents/a-1/sections" => SectionsJson,
                _ => "[]"
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    // Reflection lets the pre-implementation tests compile against the unchanged baseline.
    private void EnableFocused(bool enabled = true)
    {
        var property = typeof(PortalPreferences).GetProperty("FocusedSidebar");
        property.ShouldNotBeNull();
        property.SetValue(_prefs.Current, enabled);
    }

    [Fact]
    public void Disabled_PreservesStackedNavigation()
    {
        Seed(Conv("normal", "Normal chat"));
        var cut = RenderLayout();
        cut.FindAll("[data-testid='focused-section-trigger']").ShouldBeEmpty();
        cut.Find("[data-testid='nav-chat']").ShouldNotBeNull();
        cut.Find("[data-testid='nav-agents']").ShouldNotBeNull();
    }

    [Fact]
    public void FocusedChat_PreservesGroupsActionsAndAgentSelection()
    {
        EnableFocused();
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/agent/a-1/conversation/normal");
        SeedCollapsed("botnexus-cron-collapsed", false);
        SeedCollapsed("botnexus-webhook-collapsed", false);
        Seed(Conv("normal", "Normal chat"), Conv("pin", "Pinned chat", pinned: true),
            Conv("cron", "Scheduled chat", "Cron"), Conv("hook", "Webhook chat", "Webhook"));
        var cut = RenderLayout();
        cut.Find("[data-testid='focused-section-trigger']").TextContent.ShouldContain("Chat");
        cut.Find("[data-testid='agent-select']").GetAttribute("value").ShouldBe("a-1");
        cut.Find("[data-testid='new-conversation-btn']").ShouldNotBeNull();
        foreach (var group in new[] { "pinned", "conversations", "scheduled", "automated" })
            cut.Find($"[data-testid='conversation-group-{group}']").ShouldNotBeNull();
        cut.FindAll("[data-testid='chat-sections-body']").ShouldBeEmpty();
        cut.FindAll("[data-testid='nav-agents']").ShouldBeEmpty();
        cut.FindAll("[data-testid='nav-chat']").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusedChat_KeepsTopControlsOutsideOneKeyboardAccessibleContentScroller(bool populated)
    {
        EnableFocused();
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/agent/a-1");
        Seed(populated ? [Conv("normal", "Normal chat")] : []);
        var cut = RenderLayout();

        var scroller = cut.Find(".focused-chat-scroll");
        scroller.GetAttribute("tabindex").ShouldBe("0");
        scroller.GetAttribute("role").ShouldBe("region");
        scroller.GetAttribute("aria-label").ShouldBe("Conversation groups and sections");
        cut.FindAll(".focused-chat-scroll").ShouldHaveSingleItem();
        scroller.QuerySelector("[data-testid='agent-select']").ShouldBeNull();
        scroller.QuerySelector(".agent-conversation-list-header").ShouldBeNull();
        scroller.QuerySelector("[data-testid='new-conversation-btn']").ShouldBeNull();
        cut.Find("[data-testid='agent-select']").ShouldNotBeNull();
        cut.Find(".agent-conversation-list-header").TextContent.ShouldContain("Conversations");
        cut.Find("[data-testid='new-conversation-btn']").TextContent.ShouldContain("New");
        scroller.QuerySelectorAll(".conversation-group").Length.ShouldBe(4);
        if (!populated)
            scroller.TextContent.ShouldContain("No conversations yet.");
    }

    [Theory]
    [InlineData("none", "None")]
    [InlineData("today", "Today")]
    [InlineData("week", "This Week")]
    public void FocusedChat_FilterInteractionsPreserveBuiltInAndCustomGroups(string filter, string label)
    {
        EnableFocused();
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/agent/a-1");
        foreach (var key in new[] { "cron", "webhook", "pinned", "conversations", "sections" })
            SeedCollapsed($"botnexus-{key}-collapsed", false);
        _handler.SectionsJson = """{"sections":[{"sectionId":"sec_work","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{"custom":"sec_work"}}""";
        Seed(Conv("normal", "Normal chat"), Conv("pin", "Pinned chat", pinned: true),
            Conv("cron", "Scheduled chat", "Cron"), Conv("hook", "Webhook chat", "Webhook"),
            Conv("custom", "Custom chat"));
        var cut = RenderLayout();

        cut.Find($"[data-testid='conversation-filter-{filter}']").Click();

        var scroller = cut.Find(".focused-chat-scroll");
        foreach (var group in new[] { "pinned", "conversations", "scheduled", "automated" })
            scroller.QuerySelector($"[data-testid='conversation-group-{group}']").ShouldNotBeNull();
        cut.Find($"[data-testid='conversation-filter-{filter}']").TextContent.ShouldBe(label);
        cut.Find($"[data-testid='conversation-filter-{filter}']").ClassList.ShouldContain("active");
        scroller.QuerySelectorAll("[data-testid='conversation-filter-bar'] button")
            .Select(button => button.TextContent).ShouldBe(new[] { "None", "Today", "This Week" });
        var sectionName = scroller.QuerySelector("[data-testid='section-name']");
        sectionName.ShouldNotBeNull();
        sectionName.TextContent.ShouldBe("Work");
        var sectionMember = scroller.QuerySelector("[data-section-id='sec_work'] [data-testid='conversation-list-item']");
        sectionMember.ShouldNotBeNull();
        sectionMember.TextContent.ShouldContain("Custom chat");
        scroller.QuerySelector("[data-testid='section-add-btn']").ShouldBeNull();
        cut.Find("[data-testid='section-add-btn']").ShouldNotBeNull();
        cut.Find("[data-section-id='sec_work'] [data-testid='section-actions-trigger']").Click();
        foreach (var action in new[] { "section-rename-btn", "section-delete-btn", "section-move-up", "section-move-down" })
            cut.Find($"[data-testid='{action}']").ShouldNotBeNull();
        cut.Find("[data-testid='new-conversation-btn']").ShouldNotBeNull();
    }

    [Fact]
    public void Disabled_KeepsPeerSectionsInsideTheSameAccessibleContentScroller()
    {
        Seed(Conv("normal", "Normal chat"));
        var cut = RenderLayout();
        cut.FindAll(".focused-chat-scroll").ShouldBeEmpty();
        var list = cut.Find(".conversation-list-scroll");
        list.GetAttribute("tabindex").ShouldBe("0");
        list.QuerySelector("[data-testid='chat-sections-child']").ShouldBeNull();
        list.QuerySelectorAll(".conversation-group").Length.ShouldBe(4);
        cut.FindAll("[data-testid='chat-sections-child']").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignedRows_OverrideAllNaturalHomes_AndUnassignRestoresWithoutChangingFacts(bool focused)
    {
        EnableFocused(focused);
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/agent/a-1");
        SeedCollapsed("botnexus-cron-collapsed", false);
        SeedCollapsed("botnexus-webhook-collapsed", false);
        _handler.SectionsJson = """{"sections":[{"sectionId":"sec_work","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{"normal":"sec_work","pin":"sec_work","cron":"sec_work","hook":"sec_work","unloaded":"sec_work"}}""";
        Seed(Conv("normal", "Normal chat"), Conv("pin", "Pinned chat", pinned: true),
            Conv("cron", "Scheduled chat", "Cron"), Conv("hook", "Webhook chat", "Webhook"));
        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.FindAll("[data-section-id='sec_work'] [data-testid='conversation-list-item']").Count.ShouldBe(4));
        foreach (var id in new[] { "normal", "pin", "cron", "hook" })
        {
            var row = cut.FindAll("[data-conversation-id]").Single(e => e.GetAttribute("data-conversation-id") == id);
            row.QuerySelector("a")?.GetAttribute("href").ShouldBe($"agent/a-1/conversation/{id}");
            if (id is "cron" or "hook")
                row.QuerySelector("[data-testid='conversation-actions-trigger']").ShouldBeNull();
            else
                row.QuerySelector("[data-testid='conversation-actions-trigger']").ShouldNotBeNull();
        }
        cut.FindAll("[data-conversation-id='unloaded']").ShouldBeEmpty();
        _handler.SectionsJson = """{"sections":[{"sectionId":"sec_work","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{}}""";
        await cut.InvokeAsync(() => _ctx.Services.GetRequiredService<ConversationSectionsState>().ReloadAsync());
        foreach (var pair in new[] { ("normal", "conversations"), ("pin", "pinned"), ("cron", "scheduled"), ("hook", "automated") })
            cut.Find($"[data-testid='conversation-group-{pair.Item2}'] [data-conversation-id='{pair.Item1}']").ShouldNotBeNull();
        cut.FindAll("[data-testid='conversation-list-item']").Count.ShouldBe(4);
        var agent = _store.GetAgent("a-1");
        agent.ShouldNotBeNull();
        agent.Conversations["pin"].IsPinned.ShouldBeTrue();
        cut.Find("[data-conversation-id='cron']").TextContent.ShouldContain("Cron");
        cut.Find("[data-conversation-id='hook']").TextContent.ShouldContain("Webhook");
    }

    [Fact]
    public async Task SwitchingAgent_DiscardsDraftAndNeverShowsPreviousAgentCustomRows()
    {
        EnableFocused();
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/agent/a-1");
        _handler.SectionsJson = """{"sections":[{"sectionId":"sec_work","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{"normal":"sec_work"}}""";
        Seed(Conv("normal", "Normal chat"));
        var cut = RenderLayout();
        cut.Find("[data-testid='section-add-btn']").Click();
        cut.Find("[data-testid='section-create-input']").Input("Unsaved for Alpha");
        await cut.InvokeAsync(() =>
        {
            _store.SeedAgents([new AgentSummary("a-1", "Alpha"), new AgentSummary("b-2", "Beta")]);
            _store.SeedConversations("b-2", []);
            _store.SelectView("b-2", string.Empty, SelectionSource.UserClick);
            _store.NotifyChanged();
        });
        cut.FindAll("[data-testid='section-create-input']").ShouldBeEmpty();
        cut.FindAll("[data-section-id='sec_work']").ShouldBeEmpty();
        cut.FindAll("[data-conversation-id='normal']").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothModes_RenderCanonicalPeersWithoutParent_AndKeepNewSectionOutsideScroller(bool focused)
    {
        EnableFocused(focused);
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/agent/a-1");
        _handler.SectionsJson = """{"sections":[{"sectionId":"sec_work","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{},"groupOrder":[{"system":"Pinned"},{"sectionId":"sec_work"},{"system":"Conversations"},{"system":"Scheduled"},{"system":"Webhooks"}]}""";
        Seed();
        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.FindAll(".conversation-list-scroll > .conversation-group")
            .Select(g => g.GetAttribute("data-group-id"))
            .ShouldBe(new[] { "Pinned", "sec_work", "Conversations", "Scheduled", "Webhooks" }));
        cut.Markup.ShouldNotContain("My sections");
        cut.Find(".conversation-list-scroll").QuerySelector("[data-testid='section-add-btn']").ShouldBeNull();
        cut.Find(".agent-conversation-list-header [data-testid='section-add-btn']").Click();
        cut.Find("[data-testid='section-create-input']").GetAttribute("aria-label").ShouldBe("Section name");
        cut.Find("[data-testid='section-create-input']").Input("Unsaved");
        cut.Find("[data-testid='section-create-input']").KeyDown("Escape");
        cut.FindAll("[data-testid='section-create-input']").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("route", "conversationActionMenu.close", "section-delete-btn")]
    [InlineData("mode", "conversationActionMenu.focus", "section-move-up")]
    [InlineData("agent-aba", "conversationActionMenu.close", "section-delete-btn")]
    [InlineData("route", "conversationActionMenu.focus", "section-delete-btn")]
    [InlineData("mode", "conversationActionMenu.close", "section-move-up")]
    [InlineData("agent-aba", "conversationActionMenu.focus", "section-delete-btn")]
    public async Task HeldPeerMenu_LayoutTransitionDiscardsDetachedAction(string transition, string identifier, string action)
    {
        EnableFocused();
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/agent/a-1");
        _handler.SectionsJson = """{"sections":[{"sectionId":"sec_work","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{}}""";
        Seed();
        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.Find("[data-section-id=sec_work]").ShouldNotBeNull());
        var oldPanel = cut.FindComponents<ConversationSectionsPanel>().Single(c => !c.Instance.CreateOnly).Instance;
        var oldMenu = cut.FindComponents<ConversationSectionActionMenu>().Single(c => c.Instance.IsCustom).Instance;
        cut.Find("[data-section-id=sec_work] [data-testid=section-actions-trigger]").Click();
        var pending = _ctx.JSInterop.SetupVoid(identifier, _ => true);
        var click = cut.Find($"[data-testid={action}]").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());
        pending.Invocations.Single().Arguments[0].ShouldBe(_ctx.JSInterop.Invocations
            .Last(i => i.Identifier == "conversationActionMenu.open").Arguments[0]);
        if (transition == "route")
            await cut.InvokeAsync(() => nav.NavigateTo("/agent/a-1/conversation/next"));
        else if (transition == "mode")
        {
            EnableFocused(false);
            await cut.InvokeAsync(() => _prefs.OnChanged += Raise.Event<Action>());
        }
        else
        {
            await cut.InvokeAsync(() =>
            {
                _store.SeedAgents([new AgentSummary("a-1", "Alpha"), new AgentSummary("b-2", "Beta")]);
                _store.SeedConversations("b-2", []);
                _store.SelectView("b-2", string.Empty, SelectionSource.UserClick);
                _store.NotifyChanged();
            });
            await cut.InvokeAsync(() =>
            {
                _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
                _store.NotifyChanged();
            });
        }
        cut.WaitForAssertion(() => cut.FindComponents<ConversationSectionsPanel>().ShouldNotContain(c => ReferenceEquals(c.Instance, oldPanel)));
        cut.FindComponents<ConversationSectionActionMenu>().ShouldNotContain(c => ReferenceEquals(c.Instance, oldMenu));
        pending.SetVoidResult();
        await click;
        _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "confirm");
        _handler.Methods.ShouldAllBe(method => method == "GET");
    }

    [Fact]
    public async Task SelectionAndHistory_TrackRouteWithoutClearingIdentity()
    {
        EnableFocused();
        Seed(Conv("normal", "Normal chat"));
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/agent/a-1/conversation/normal?tab=chat");
        var cut = RenderLayout();
        cut.Find("[data-testid='focused-section-trigger']").Click();
        cut.Find("[data-section='agents']").Click();
        nav.Uri.ShouldEndWith("/agents");
        cut.Find("[data-testid='focused-section-trigger']").TextContent.ShouldContain("Agents");
        cut.Find("[data-testid='subnav-agents-new']").ShouldNotBeNull();
        cut.Find("[data-testid='subnav-agents-a-1']").TextContent.ShouldContain("Alpha");
        cut.FindAll("[data-testid='agent-select']").ShouldBeEmpty();
        _store.ActiveAgentId.ShouldBe("a-1");
        await cut.InvokeAsync(() => nav.NavigateTo("/agent/a-1/conversation/normal"));
        cut.Find("[data-testid='focused-section-trigger']").TextContent.ShouldContain("Chat");
        EnableFocused(false);
        await cut.InvokeAsync(() => _prefs.OnChanged += Raise.Event<Action>());
        cut.Find("[data-testid='nav-agents']").ShouldNotBeNull();
        cut.FindAll("[data-testid='focused-section-trigger']").ShouldBeEmpty();
        EnableFocused();
        await cut.InvokeAsync(() => _prefs.OnChanged += Raise.Event<Action>());
        cut.Find("[data-testid='focused-section-trigger']").TextContent.ShouldContain("Chat");
    }

    [Fact]
    public void Picker_ListsExistingIconsAndDismissesOnEscape()
    {
        EnableFocused();
        var cut = RenderLayout();
        var trigger = cut.Find("[data-testid='focused-section-trigger']");
        trigger.GetAttribute("aria-expanded").ShouldBe("false");
        trigger.Click();
        var options = cut.FindAll("[role='menuitemradio']");
        options.Select(x => x.GetAttribute("data-section")).ShouldBe(new[]
            { "home", "activity", "tools", "chat", "configuration", "agents", "cron", "plugins", "guide" });
        options.ShouldAllBe(x => x.QuerySelector("svg") != null);
        cut.Find("[role='menu']").KeyDown("Escape");
        cut.FindAll("[role='menu']").ShouldBeEmpty();
        cut.Find("[data-testid='focused-section-trigger']").GetAttribute("aria-expanded").ShouldBe("false");
    }

    [Theory]
    [InlineData("/agents?tab=overview")]
    [InlineData("/agents#list")]
    public void FocusedAgents_QueryOrFragment_KeepsExistingAgentList(string route)
    {
        EnableFocused();
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(route);
        var cut = RenderLayout();
        cut.Find("[data-testid='focused-section-trigger']").TextContent.ShouldContain("Agents");
        cut.Find("[data-testid='subnav-agents-a-1']").TextContent.ShouldContain("Alpha");
        cut.Find("[data-testid='subnav-agents-new']").ShouldNotBeNull();
    }

    [Fact]
    public void FocusedTools_ShowsAvailableToolsWithoutOrphanCollapseControl()
    {
        EnableFocused();
        _handler.ToolsJson = "[{\"id\":\"sample\",\"name\":\"Sample tool\",\"url\":\"https://example.com\",\"order\":1}]";
        SeedCollapsed("botnexus-tools-collapsed", true);
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/tools");
        var cut = RenderLayout();
        cut.Find("[data-testid='tools-subnav']").TextContent.ShouldContain("Sample tool");
        cut.FindAll("[data-testid='tools-collapse-toggle']").ShouldBeEmpty();
    }

    [Fact]
    public void EmptyAgents_KeepsAddActionWithoutInventingItems()
    {
        EnableFocused();
        _handler.AgentsJson = "[]";
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/agents");
        var cut = RenderLayout();
        cut.Find("[data-testid='focused-section-content']").GetAttribute("data-section").ShouldBe("agents");
        cut.FindAll(".sidebar-subnav-item").ShouldHaveSingleItem().TextContent.ShouldContain("Add Agent");
    }

    [Fact]
    public void Picker_UsesConfiguredOrderAndFiltersUnavailableSkills()
    {
        EnableFocused();
        _handler.NavOrderJson = "[{\"key\":\"agents\",\"order\":1},{\"key\":\"home\",\"order\":2},{\"key\":\"chat\",\"order\":3}]";
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(new NavOrderApiClient(http));
        var cut = RenderLayout();
        cut.Find("[data-testid='focused-section-trigger']").Click();
        var keys = cut.FindAll("[role='menuitemradio']").Select(x => x.GetAttribute("data-section")).ToList();
        keys.IndexOf("agents").ShouldBeLessThan(keys.IndexOf("home"));
        keys.IndexOf("home").ShouldBeLessThan(keys.IndexOf("chat"));
        keys.ShouldNotContain("skills");
    }

    [Fact]
    public async Task Picker_AvailabilityChangesFollowFeatureState()
    {
        EnableFocused();
        var rest = _ctx.Services.GetRequiredService<IGatewayRestClient>();
        rest.GetExtensionDetailsAsync(Arg.Any<CancellationToken>()).Returns([
            new ExtensionDetailDto("botnexus-skills", "Skills", "1.0.0", true, [], [], "skills.dll", [])
        ]);
        var features = _ctx.Services.GetRequiredService<ExtensionFeatureService>();
        var cut = RenderLayout();
        await cut.InvokeAsync(() => features.LoadAsync());
        cut.Find("[data-testid='focused-section-trigger']").Click();
        cut.Find("[role='menuitemradio'][data-section='skills']").TextContent.ShouldContain("Skills");
        cut.Find("[role='menuitemradio'][data-section='skills']").Click();
        cut.Find("[data-testid='focused-section-trigger']").TextContent.ShouldContain("Skills");
        rest.GetExtensionDetailsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ExtensionDetailDto>());
        await cut.InvokeAsync(() => features.LoadAsync());
        cut.Find("[data-testid='focused-section-trigger']").TextContent.ShouldContain("Home");
    }

    [Theory]
    [InlineData("/chat")]
    [InlineData("/agent/a-1")]
    [InlineData("/chat/a-1/normal?tab=files#messages")]
    public void EmptyChat_RendersExistingEmptyState(string route)
    {
        EnableFocused();
        Seed();
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(route);
        var cut = RenderLayout();
        cut.Find("[data-testid='focused-section-content']").TextContent.ShouldContain("No conversations yet.");
        cut.FindAll("[data-testid='conversation-list-item']").ShouldBeEmpty();
    }

    [Fact]
    public void Picker_ArrowKeysHomeEndAndOutsideDismissal()
    {
        EnableFocused();
        var cut = RenderLayout();
        cut.Find("[data-testid='focused-section-trigger']").KeyDown("ArrowDown");
        cut.Find("[role='menu']").KeyDown("End");
        cut.Find("[role='menuitemradio'][data-section='guide']").GetAttribute("tabindex").ShouldBe("0");
        cut.Find("[role='menu']").KeyDown("ArrowDown");
        cut.Find("[role='menuitemradio'][data-section='home']").GetAttribute("tabindex").ShouldBe("0");
        cut.Find("[role='menu']").KeyDown("ArrowUp");
        cut.Find("[role='menuitemradio'][data-section='guide']").GetAttribute("tabindex").ShouldBe("0");
        cut.Find("[role='menu']").KeyDown("Home");
        cut.Find("[role='menuitemradio'][data-section='home']").GetAttribute("tabindex").ShouldBe("0");
        cut.Find(".focused-picker-dismiss").Click();
        cut.FindAll("[role='menu']").ShouldBeEmpty();
        _ctx.JSInterop.Invocations["Blazor._internal.domWrapper.focus"].ShouldNotBeEmpty();
    }

    [Fact]
    public void ReselectChat_KeepsConversationDeepLink()
    {
        EnableFocused();
        Seed(Conv("normal", "Normal chat"));
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/agent/a-1/conversation/normal");
        var cut = RenderLayout();
        cut.Find("[data-testid='focused-section-trigger']").Click();
        cut.Find("[data-section='chat'][role='menuitemradio']").Click();
        nav.Uri.ShouldEndWith("/agent/a-1/conversation/normal");
    }

    [Theory]
    [InlineData("conversationActionMenu.close", "replacement")]
    [InlineData("conversationActionMenu.focus", "replacement")]
    [InlineData("conversationActionMenu.close", "removal")]
    [InlineData("conversationActionMenu.focus", "removal")]
    [InlineData("conversationActionMenu.close", "reorder")]
    [InlineData("conversationActionMenu.focus", "reorder")]
    public async Task MainLayout_RowListTransitionCancelsHeldClickWithEquivalentActions(string identifier, string transition)
    {
        EnableFocused();
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/agent/a-1");
        SeedCollapsed("botnexus-conversations-collapsed", false);
        var epoch = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var first = Conv("row-a", "A") with { UpdatedAt = epoch.AddMinutes(2) };
        var second = Conv("row-b", "B") with { UpdatedAt = epoch.AddMinutes(1) };
        var replacement = Conv("row-c", "C") with { UpdatedAt = epoch.AddMinutes(3) };
        Seed(first, second);
        var cut = RenderLayout();
        var original = cut.FindComponents<ConversationActionMenu>().First();
        original.Instance.SubjectAgentId.ShouldBe("a-1");
        original.Instance.SubjectConversationId.ShouldBe("row-a");
        var metadata = original.Instance.Actions.Select(a => (a.Id, a.Label, a.IsDestructive)).ToArray();
        original.Find("[data-testid='conversation-actions-trigger']").Click();
        var pending = _ctx.JSInterop.SetupVoid(identifier, _ => true);
        var click = original.Find("[data-action-id=pin]").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());

        await cut.InvokeAsync(() =>
        {
            _store.SeedConversations("a-1", transition switch
            {
                "replacement" => [replacement, second],
                "removal" => [second],
                _ => [first, second with { UpdatedAt = epoch.AddMinutes(3) }]
            });
            _store.NotifyChanged();
        });
        var expected = transition == "replacement" ? "row-c" : "row-b";
        cut.WaitForAssertion(() => cut.FindComponents<ConversationActionMenu>().First()
            .Instance.SubjectConversationId.ShouldBe(expected));
        var current = cut.FindComponents<ConversationActionMenu>().First();
        // Exercise actual positional row reuse through MainLayout + ConversationSectionsPanel,
        // not an artificial list that supplies the right identity on the product's behalf.
        current.Instance.ShouldBeSameAs(original.Instance);
        current.Instance.Actions.Select(a => (a.Id, a.Label, a.IsDestructive)).ShouldBe(metadata);
        pending.SetVoidResult();
        await click;
        var interaction = _ctx.Services.GetRequiredService<IAgentInteractionService>();
        await interaction.DidNotReceive().SetConversationPinnedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        cut.FindAll("[data-testid='conversation-action-menu']").ShouldBeEmpty();

        // The reused row's newly authorized click still works and targets only its current subject.
        current.Find("[data-testid='conversation-actions-trigger']").Click();
        await current.Find("[data-action-id=pin]").ClickAsync(new());
        await interaction.Received(1).SetConversationPinnedAsync("a-1", expected, true);
        await interaction.DidNotReceive().SetConversationPinnedAsync("a-1", "row-a", Arg.Any<bool>());
    }

    [Fact]
    public async Task Settings_ExposesLabelledOffByDefaultToggle()
    {
        var cut = _ctx.Render<PortalSettingsPanel>();
        await cut.InvokeAsync(cut.Instance.Open);
        var toggle = cut.Find("[data-testid='focused-sidebar-toggle']");
        toggle.HasAttribute("checked").ShouldBeFalse();
        cut.Find($"label[for='{toggle.Id}']").TextContent.ShouldBe("Focused sidebar");
        toggle.Change(true);
        // Observe persistence via the real service in the preference tests below.
        await _prefs.Received(1).SetFocusedSidebarAsync(true);
        toggle.Change(false);
        await _prefs.Received(1).SetFocusedSidebarAsync(false);
    }
}
