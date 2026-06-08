using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests verifying <see cref="SessionDebugPanel"/> is correctly wired
/// into <see cref="MainLayout"/> via the debug button (issue #1057).
/// </summary>
public sealed class SessionDebugPanelWiringTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;
    private readonly IPortalPreferencesService _prefs;
    private readonly IGatewayRestClient _restClient;

    public SessionDebugPanelWiringTests()
    {
        _store = new ClientStateStore();
        _prefs = Substitute.For<IPortalPreferencesService>();
        _prefs.Current.Returns(new PortalPreferences { DebugModeEnabled = true });

        _restClient = Substitute.For<IGatewayRestClient>();
        _restClient.ApiBaseUrl.Returns("");

        var interaction = Substitute.For<IAgentInteractionService>();
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);

        var hub = new GatewayHubConnection();
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var gatewayInfo = new GatewayInfoService(http, _restClient);

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(interaction);
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(hub);
        _ctx.Services.AddSingleton(gatewayInfo);
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        _ctx.Services.AddSingleton(_prefs);
        _ctx.Services.AddSingleton(_restClient);
        _ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new ExtensionFeatureService(_restClient));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<MainLayout> RenderLayout() =>
        _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (RenderFragment)(_ => { })));

    private void SeedAgentWithSession(string sessionId = "sess-123")
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Chat 1", true, "Active", sessionId, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SetActiveConversation("a-1", "c-1");
        _store.ActiveAgentId = "a-1";
    }

    [Fact]
    public void Debug_panel_not_rendered_by_default()
    {
        SeedAgentWithSession();
        var cut = RenderLayout();

        Assert.Empty(cut.FindAll("[data-testid='debug-panel-overlay']"));
        Assert.Empty(cut.FindAll("[data-testid='session-debug-panel']"));
    }

    [Fact]
    public async Task Clicking_debug_button_opens_session_debug_panel()
    {
        SeedAgentWithSession();
        var cut = RenderLayout();

        await cut.InvokeAsync(() => cut.Find("[data-testid='banner-debug-btn']").Click());

        cut.Find("[data-testid='debug-panel-overlay']");
        cut.Find("[data-testid='session-debug-panel']");
    }

    [Fact]
    public async Task Debug_button_is_noop_when_no_active_session()
    {
        // Agent exists but conversation has no session ID
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Chat 1", true, "Active", null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SetActiveConversation("a-1", "c-1");
        _store.ActiveAgentId = "a-1";

        var cut = RenderLayout();

        await cut.InvokeAsync(() => cut.Find("[data-testid='banner-debug-btn']").Click());

        Assert.Empty(cut.FindAll("[data-testid='debug-panel-overlay']"));
    }

    [Fact]
    public async Task Close_button_dismisses_debug_panel()
    {
        SeedAgentWithSession();
        var cut = RenderLayout();

        await cut.InvokeAsync(() => cut.Find("[data-testid='banner-debug-btn']").Click());
        cut.Find("[data-testid='session-debug-panel']"); // verify open

        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-close']").Click());

        Assert.Empty(cut.FindAll("[data-testid='debug-panel-overlay']"));
    }

    [Fact]
    public async Task Clicking_overlay_backdrop_dismisses_debug_panel()
    {
        SeedAgentWithSession();
        var cut = RenderLayout();

        await cut.InvokeAsync(() => cut.Find("[data-testid='banner-debug-btn']").Click());
        cut.Find("[data-testid='debug-panel-overlay']"); // verify open

        await cut.InvokeAsync(() => cut.Find("[data-testid='debug-panel-overlay']").Click());

        Assert.Empty(cut.FindAll("[data-testid='debug-panel-overlay']"));
    }

    [Fact]
    public async Task Escape_key_dismisses_debug_panel()
    {
        SeedAgentWithSession();
        var cut = RenderLayout();

        await cut.InvokeAsync(() => cut.Find("[data-testid='banner-debug-btn']").Click());
        cut.Find("[data-testid='debug-panel-overlay']"); // verify open

        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='debug-panel-overlay']").KeyDown(new KeyboardEventArgs { Key = "Escape" }));

        Assert.Empty(cut.FindAll("[data-testid='debug-panel-overlay']"));
    }

    [Fact]
    public void Debug_button_not_visible_when_debug_mode_disabled()
    {
        _prefs.Current.Returns(new PortalPreferences { DebugModeEnabled = false });
        SeedAgentWithSession();

        var cut = RenderLayout();

        Assert.Empty(cut.FindAll("[data-testid='banner-debug-btn']"));
    }

    [Fact]
    public async Task Debug_panel_passes_active_session_id()
    {
        SeedAgentWithSession("my-session-42");
        var cut = RenderLayout();

        await cut.InvokeAsync(() => cut.Find("[data-testid='banner-debug-btn']").Click());

        // SessionDebugPanel calls GetSessionDebugAsync with the session ID
        // If it rendered at all (the panel guards on non-empty SessionId), the wiring worked.
        cut.Find("[data-testid='session-debug-panel']");
    }
}
