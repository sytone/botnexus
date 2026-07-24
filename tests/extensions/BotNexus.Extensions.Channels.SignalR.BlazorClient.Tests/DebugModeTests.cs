using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class DebugModeTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;
    private readonly IPortalPreferencesService _prefs;

    public DebugModeTests()
    {
        _store = new ClientStateStore();
        _prefs = Substitute.For<IPortalPreferencesService>();
        _prefs.Current.Returns(new PortalPreferences());

        var hub = new GatewayHubConnection();
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var gatewayInfo = new GatewayInfoService(http, restClient);

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IPortalLoadService>());
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
            .Add(c => c.Body, (RenderFragment)(_ => { })));

    [Fact]
    public void Debug_button_is_hidden_when_DebugModeEnabled_is_false()
    {
        _prefs.Current.Returns(new PortalPreferences { DebugModeEnabled = false });

        var cut = RenderLayout();

        cut.FindAll("[data-testid='banner-debug-btn']").Count.ShouldBe(0);
    }

    [Fact]
    public void Debug_button_is_visible_when_DebugModeEnabled_is_true()
    {
        _prefs.Current.Returns(new PortalPreferences { DebugModeEnabled = true });

        var cut = RenderLayout();

        cut.Find("[data-testid='banner-debug-btn']").ShouldNotBeNull();
    }

    [Fact]
    public async Task PortalSettingsPanel_contains_debug_mode_toggle()
    {
        _prefs.Current.Returns(new PortalPreferences());
        var settingsPanel = _ctx.Render<PortalSettingsPanel>();

        // Open the panel (it is hidden by default)
        await settingsPanel.InvokeAsync(() => settingsPanel.Instance.Open());

        settingsPanel.Find("[data-testid='debug-mode-toggle']").ShouldNotBeNull();
    }

    [Fact]
    public async Task PortalSettingsPanel_contains_archive_confirm_toggle()
    {
        _prefs.Current.Returns(new PortalPreferences());
        var settingsPanel = _ctx.Render<PortalSettingsPanel>();

        await settingsPanel.InvokeAsync(() => settingsPanel.Instance.Open());

        settingsPanel.Find("[data-testid='archive-confirm-toggle']").ShouldNotBeNull();
    }

    [Fact]
    public async Task PortalSettingsPanel_archive_confirm_toggle_persists_preference()
    {
        _prefs.Current.Returns(new PortalPreferences());
        var settingsPanel = _ctx.Render<PortalSettingsPanel>();

        await settingsPanel.InvokeAsync(() => settingsPanel.Instance.Open());
        var toggle = settingsPanel.Find("[data-testid='archive-confirm-toggle']");
        await settingsPanel.InvokeAsync(() => toggle.Change(false));

        await _prefs.Received(1).SetArchiveConfirmAsync(false);
    }

    [Fact]
    public void Debug_button_appears_after_DebugModeEnabled_preference_changes()
    {
        var prefs = new PortalPreferences { DebugModeEnabled = false };
        _prefs.Current.Returns(prefs);

        var cut = RenderLayout();
        cut.FindAll("[data-testid='banner-debug-btn']").Count.ShouldBe(0);

        // Simulate preference change
        prefs.DebugModeEnabled = true;
        _prefs.OnChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='banner-debug-btn']").Count.ShouldBe(1));
    }
}