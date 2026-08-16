using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AgentPanelVerticalSliceTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IPortalLoadService _portalLoad = Substitute.For<IPortalLoadService>();

    public AgentPanelVerticalSliceTests()
    {
        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _store.SeedAgents([new AgentSummary("agent-1", "Alpha")]);
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                ConversationId: "conv-1",
                AgentId: "agent-1",
                Title: "General",
                IsDefault: true,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        // #3064: Home injects the per-agent conversation MRU at the route seam.
        _ctx.Services.AddSingleton<IConversationMruService, ConversationMruService>();
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Agent_selection_renders_agent_panel_shell_container()
    {
        var cut = RenderHomeForAgentConversation();
        Assert.Single(cut.FindAll("[data-testid='agent-panel']"));
    }

    [Fact]
    public void Agent_panel_renders_expected_tab_strip_labels()
    {
        var cut = RenderHomeForAgentConversation();

        var tabLabels = cut.FindAll(".agent-panel-tab .agent-tab-label").Select(label => label.TextContent.Trim()).ToArray();
        Assert.Equal(["Conversation", "Workspace", "Reports", "Canvas", "Todo"], tabLabels);

        Assert.Contains("data-testid=\"workspace-panel\"", cut.Markup);
        Assert.Contains("data-testid=\"reports-panel\"", cut.Markup);
        Assert.Contains("data-testid=\"canvas-panel\"", cut.Markup);
        Assert.Contains("data-testid=\"todo-panel\"", cut.Markup);
    }

    [Fact]
    public void Reports_tab_activates_reports_panel()
    {
        var restClient = _ctx.Services.GetRequiredService<IGatewayRestClient>();
        restClient.GetReportsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReportListItemDto>>([]));

        var cut = RenderHomeForAgentConversation();

        cut.Find(".agent-panel-tab[data-tab='reports']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='reports']"));
            Assert.NotNull(cut.Find("[data-testid='reports-panel']"));
        });
    }

    [Fact]
    public void Canvas_query_parameter_activates_canvas_tab()
    {
        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/chat/agent-1/conv-1?tab=canvas");

        var cut = RenderHomeForAgentConversation();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='canvas']"));
            Assert.NotNull(cut.Find("[data-testid='canvas-panel']"));
        });
    }

    [Fact]
    public void Conversation_tab_is_default_active_tab()
    {
        var cut = RenderHomeForAgentConversation();
        Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='conversation']"));
    }

    [Fact]
    public void Conversation_tab_hosts_existing_chat_surface()
    {
        var cut = RenderHomeForAgentConversation();

        Assert.NotNull(cut.Find("[data-testid='agent-panel-conversation'] .chat-panel"));
    }

    // #637 — AgentPanel tab URL deep-link fixes

    [Fact]
    public void Workspace_query_parameter_activates_workspace_tab_for_regular_agent()
    {
        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/chat/agent-1/conv-1?tab=workspace");

        var cut = RenderHomeForAgentConversation();

        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='workspace']")));
    }

    [Fact]
    public void SubAgent_workspace_tab_url_is_suppressed_to_conversation()
    {
        // #2248: a GENUINE sub-agent observer entry (immutable IsObserverAgent) suppresses Workspace.
        SeedAgentOneAsObserver();

        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/chat/agent-1/conv-1?tab=workspace");

        var cut = RenderHomeForAgentConversation();

        // Sub-agent should not show workspace; fall back to conversation
        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='conversation']")));
    }

    [Fact]
    public void Tab_is_reapplied_when_store_changes_after_initial_render()
    {
        // Start with agent-1 as a regular agent and canvas tab in URL
        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/chat/agent-1/conv-1?tab=canvas");

        var cut = RenderHomeForAgentConversation();

        // Canvas tab should be active for regular agent
        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='canvas']")));

        // Simulate agent becoming a sub-agent after data arrives (store change)
        _store.RegisterSession("agent-1", "session-sub-1", "signalr", "agent-subagent");
        _store.NotifyChanged();

        // Sub-agent canvas IS allowed (only workspace/reports suppressed), so canvas remains
        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='canvas']")));
    }

    [Fact]
    public void SubAgent_reports_tab_url_is_suppressed_to_conversation()
    {
        // #2248: a GENUINE sub-agent observer entry (immutable IsObserverAgent) suppresses Reports.
        SeedAgentOneAsObserver();

        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/chat/agent-1/conv-1?tab=reports");

        var cut = RenderHomeForAgentConversation();

        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='conversation']")));
    }

    [Fact]
    public void App_css_contains_agent_panel_mobile_responsive_hooks()
    {
        var cssPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot",
            "css",
            "app.css");

        var css = File.ReadAllText(cssPath);

        Assert.Contains(".agent-panel", css);
        Assert.Contains(".agent-panel-header", css);
        Assert.Contains(".agent-panel-tab-strip", css);
        Assert.Contains(".agent-panel-tab", css);
        Assert.Contains(".canvas-panel", css);
        Assert.Contains("@media (max-width: 768px)", css);

        // #2441: portal chrome spacing is expressed as themeable density tokens with both a
        // compact and a comfortable value set, not hard-coded numbers.
        Assert.Contains("[data-density=\"compact\"]", css);
        Assert.Contains("[data-density=\"comfortable\"]", css);
        foreach (var token in new[]
                 {
                     "--density-row-pad-y", "--density-row-pad-x", "--density-gap",
                     "--density-control-h", "--density-bar-h", "--density-font-sm",
                     "--density-font-xs", "--density-nav-pad-y", "--density-nav-pad-x",
                     "--density-subnav-indent", "--density-group-pad-y"
                 })
        {
            Assert.Contains(token, css);
        }
    }

    private IRenderedComponent<Home> RenderHomeForAgentConversation() =>
        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "conv-1"));

    // #2248: re-seed agent-1 as a GENUINE sub-agent observer (immutable IsObserverAgent) while
    // preserving its seeded conversation, so tests that need a real sub-agent no longer rely on
    // session-type poisoning (which no longer classifies a user agent as a sub-agent).
    private void SeedAgentOneAsObserver()
    {
        _store.RemoveAgent("agent-1");
        _store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            SessionType = "agent-subagent",
            IsObserverAgent = true,
            IsConnected = true
        });
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                ConversationId: "conv-1",
                AgentId: "agent-1",
                Title: "General",
                IsDefault: true,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("agent-1", string.Empty, SelectionSource.SubAgentView);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate BotNexus.slnx from test base directory.");
    }
}