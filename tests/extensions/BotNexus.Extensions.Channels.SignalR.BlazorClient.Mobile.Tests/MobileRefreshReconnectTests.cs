using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #825: mobile refresh button and auto-reconnect on app resume.
/// </summary>
public sealed class MobileRefreshReconnectTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IPortalLoadService _portalLoad;
    private readonly IAgentInteractionService _interaction;

    public MobileRefreshReconnectTests()
    {
        _store = Substitute.For<IClientStateStore>();
        _portalLoad = Substitute.For<IPortalLoadService>();
        _interaction = Substitute.For<IAgentInteractionService>();

        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.IsSignalRConnected.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _portalLoad.RefreshAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _portalLoad.ResumeAsync(Arg.Any<CancellationToken>()).Returns(HubResumeOutcome.Alive);
        _store.Agents.Returns(new Dictionary<string, AgentState>().AsReadOnly());
        _store.ActiveAgentId.Returns((string?)null);
        _store.GetStreamState(Arg.Any<string>()).Returns(new ConversationStreamState());
        _store.GetMessages(Arg.Any<string>()).Returns(new List<ChatMessage>().AsReadOnly());

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    // ── Refresh button presence ──────────────────────────────────────────────

    [Fact]
    public void Refresh_button_is_rendered_in_top_bar_when_portal_is_ready()
    {
        var cut = _ctx.Render<Chat>();
        var btn = cut.Find("[data-testid='refresh-btn']");
        Assert.NotNull(btn);
    }

    [Fact]
    public void Refresh_button_is_enabled_when_not_refreshing()
    {
        var cut = _ctx.Render<Chat>();
        var btn = cut.Find("[data-testid='refresh-btn']");
        Assert.Null(btn.GetAttribute("disabled"));
    }

    // ── Disconnected indicator ───────────────────────────────────────────────

    [Fact]
    public void Disconnected_bar_is_hidden_when_SignalR_is_connected()
    {
        _portalLoad.IsSignalRConnected.Returns(true);
        var cut = _ctx.Render<Chat>();
        Assert.Empty(cut.FindAll("[data-testid='disconnected-bar']"));
    }

    [Fact]
    public void Disconnected_bar_is_shown_when_SignalR_is_disconnected()
    {
        _portalLoad.IsSignalRConnected.Returns(false);
        var cut = _ctx.Render<Chat>();
        Assert.NotEmpty(cut.FindAll("[data-testid='disconnected-bar']"));
    }

    [Fact]
    public void Disconnected_bar_appears_when_connection_state_changes_to_disconnected()
    {
        _portalLoad.IsSignalRConnected.Returns(true);
        var cut = _ctx.Render<Chat>();

        // Verify no bar initially
        Assert.Empty(cut.FindAll("[data-testid='disconnected-bar']"));

        // Simulate connection drop
        _portalLoad.IsSignalRConnected.Returns(false);
        _portalLoad.OnConnectionStateChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
            Assert.NotEmpty(cut.FindAll("[data-testid='disconnected-bar']")));
    }

    // ── Manual refresh invokes PortalLoad.RefreshAsync ───────────────────────

    [Fact]
    public void Clicking_refresh_button_calls_PortalLoad_RefreshAsync()
    {
        var cut = _ctx.Render<Chat>();
        cut.Find("[data-testid='refresh-btn']").Click();

        _portalLoad.Received(1).RefreshAsync(Arg.Any<CancellationToken>());
    }

    // ── Refresh button renders Unicode not HTML entities ─────────────────────

    [Fact]
    public void Refresh_button_shows_unicode_arrow_not_html_entity()
    {
        var cut = _ctx.Render<Chat>();
        var btn = cut.Find("[data-testid='refresh-btn']");
        // Should render the actual Unicode character ↺ not the entity string
        Assert.DoesNotContain("&#x21BA;", btn.InnerHtml);
        Assert.Contains("\u21ba", btn.TextContent);
    }

    // ── OnAppResumed drives the liveness-verified hub reset (#1838) ───────────

    [Fact]
    public async Task OnAppResumed_calls_PortalLoad_ResumeAsync()
    {
        var cut = _ctx.Render<Chat>();
        await cut.Instance.OnAppResumed();

        // #1838: resume must go through the liveness-verified reset (probe-then-rebuild),
        // not a bare RefreshAsync that trusts the possibly-zombie connection state.
        await _portalLoad.Received(1).ResumeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnAppResumed_does_not_bypass_reset_with_bare_RefreshAsync()
    {
        var cut = _ctx.Render<Chat>();
        await cut.Instance.OnAppResumed();

        // The component must delegate to ResumeAsync (which internally probes and, only on
        // failure, rebuilds + refreshes). It must not call RefreshAsync directly, which would
        // skip the liveness probe and leave an iOS zombie socket in place.
        await _portalLoad.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>());
    }

    // ── IPortalLoadService.OnConnectionStateChanged subscription ────────────

    [Fact]
    public void Dispose_unsubscribes_from_OnConnectionStateChanged()
    {
        var cut = _ctx.Render<Chat>();
        cut.Instance.Dispose();

        // Raising after dispose should not throw or cause re-render
        _portalLoad.OnConnectionStateChanged += Raise.Event<Action>();
    }
}
