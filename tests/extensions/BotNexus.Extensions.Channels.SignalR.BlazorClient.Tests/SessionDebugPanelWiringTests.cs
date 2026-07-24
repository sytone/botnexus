using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SessionDebugPanelWiringTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;
    private readonly IPortalPreferencesService _prefs;

    public SessionDebugPanelWiringTests()
    {
        _store = new ClientStateStore();
        var interaction = Substitute.For<IAgentInteractionService>();
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);

        var hub = new GatewayHubConnection();
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var gatewayInfo = new GatewayInfoService(http, restClient);

        _prefs = Substitute.For<IPortalPreferencesService>();
        _prefs.Current.Returns(new PortalPreferences { DebugModeEnabled = true });

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(hub);
        _ctx.Services.AddSingleton(gatewayInfo);
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        _ctx.Services.AddSingleton(_prefs);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
        _ctx.Services.AddSingleton(new CronApiClient(http));
        _ctx.Services.AddSingleton(new SectionsApiClient(http));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<MainLayout> RenderLayout() =>
        _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (Microsoft.AspNetCore.Components.RenderFragment)(_ => { })));

    [Fact]
    public void Debug_button_visible_when_debug_mode_enabled()
    {
        var cut = RenderLayout();
        var btn = cut.Find("[data-testid='banner-debug-btn']");
        Assert.NotNull(btn);
    }

    [Fact]
    public void Debug_button_hidden_when_debug_mode_disabled()
    {
        _prefs.Current.Returns(new PortalPreferences { DebugModeEnabled = false });
        var cut = RenderLayout();
        Assert.Empty(cut.FindAll("[data-testid='banner-debug-btn']"));
    }

    [Fact]
    public async Task Clicking_debug_button_shows_session_debug_panel()
    {
        SetupAgentWithSession();
        var cut = RenderLayout();

        // Panel not visible initially.
        Assert.Empty(cut.FindAll("[data-testid='session-debug-panel']"));

        // Click debug button.
        await cut.InvokeAsync(() => cut.Find("[data-testid='banner-debug-btn']").Click());

        // Panel should now be visible.
        cut.Find("[data-testid='session-debug-panel']");
    }

    [Fact]
    public async Task Clicking_debug_button_twice_hides_panel()
    {
        SetupAgentWithSession();
        var cut = RenderLayout();

        await cut.InvokeAsync(() => cut.Find("[data-testid='banner-debug-btn']").Click());
        cut.Find("[data-testid='session-debug-panel']");

        await cut.InvokeAsync(() => cut.Find("[data-testid='banner-debug-btn']").Click());
        Assert.Empty(cut.FindAll("[data-testid='session-debug-panel']"));
    }

    [Fact]
    public async Task Close_button_on_debug_panel_hides_it()
    {
        SetupAgentWithSession();
        var cut = RenderLayout();
        await cut.InvokeAsync(() => cut.Find("[data-testid='banner-debug-btn']").Click());
        cut.Find("[data-testid='session-debug-panel']");

        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-close']").Click());
        Assert.Empty(cut.FindAll("[data-testid='session-debug-panel']"));
    }

    private void SetupAgentWithSession()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "TestAgent")]);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);
        var agent = _store.GetAgent("agent-1")!;
        agent.ActiveConversationId = "conv-1";
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            ActiveSessionId = "session-123"
        };
        _store.NotifyChanged();
    }
}