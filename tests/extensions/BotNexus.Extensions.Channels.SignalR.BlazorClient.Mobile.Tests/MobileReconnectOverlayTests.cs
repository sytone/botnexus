using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the mobile auto-retrying reconnect overlay (#1839). The overlay replaces the raw
/// <c>#blazor-error-ui</c> banner as the user-facing experience for a transient background drop:
/// it shows a friendly "Reconnecting..." state, auto-retries via the liveness-verified resume
/// path (#1838), and dismisses automatically once the hub is live again.
/// </summary>
public sealed class MobileReconnectOverlayTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IPortalLoadService _portalLoad = Substitute.For<IPortalLoadService>();

    public MobileReconnectOverlayTests()
    {
        _portalLoad.ResumeAsync(Arg.Any<CancellationToken>()).Returns(HubResumeOutcome.Alive);
        _portalLoad.RefreshAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Overlay_is_hidden_while_connected()
    {
        _portalLoad.IsSignalRConnected.Returns(true);
        var cut = _ctx.Render<ReconnectOverlay>();
        Assert.Empty(cut.FindAll("[data-testid='reconnect-overlay']"));
    }

    [Fact]
    public void Overlay_is_hidden_on_initial_load_before_first_connect()
    {
        // A fresh page that has never connected yet must NOT flash the reconnect overlay during
        // the initial connect handshake -- that path is "connecting", not "reconnecting".
        _portalLoad.IsSignalRConnected.Returns(false);
        var cut = _ctx.Render<ReconnectOverlay>();
        Assert.Empty(cut.FindAll("[data-testid='reconnect-overlay']"));
    }

    [Fact]
    public void Overlay_appears_after_an_established_connection_drops()
    {
        _portalLoad.IsSignalRConnected.Returns(true);
        var cut = _ctx.Render<ReconnectOverlay>();
        Assert.Empty(cut.FindAll("[data-testid='reconnect-overlay']"));

        // Simulate a transient background drop of a previously-live connection.
        _portalLoad.IsSignalRConnected.Returns(false);
        _portalLoad.OnConnectionStateChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
            Assert.NotEmpty(cut.FindAll("[data-testid='reconnect-overlay']")));
    }

    [Fact]
    public void Overlay_shows_friendly_reconnecting_text_not_the_raw_banner()
    {
        _portalLoad.IsSignalRConnected.Returns(true);
        var cut = _ctx.Render<ReconnectOverlay>();
        _portalLoad.IsSignalRConnected.Returns(false);
        _portalLoad.OnConnectionStateChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
        {
            var overlay = cut.Find("[data-testid='reconnect-overlay']");
            Assert.Contains("Reconnecting", overlay.TextContent);
            Assert.DoesNotContain("unhandled error", overlay.TextContent, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Overlay_dismisses_automatically_once_the_hub_is_live_again()
    {
        _portalLoad.IsSignalRConnected.Returns(true);
        var cut = _ctx.Render<ReconnectOverlay>();

        _portalLoad.IsSignalRConnected.Returns(false);
        _portalLoad.OnConnectionStateChanged += Raise.Event<Action>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='reconnect-overlay']")));

        // Hub live again -> overlay must self-dismiss.
        _portalLoad.IsSignalRConnected.Returns(true);
        _portalLoad.OnConnectionStateChanged += Raise.Event<Action>();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='reconnect-overlay']")));
    }

    [Fact]
    public async Task Retry_button_drives_the_liveness_verified_resume_path()
    {
        _portalLoad.IsSignalRConnected.Returns(true);
        var cut = _ctx.Render<ReconnectOverlay>();
        _portalLoad.IsSignalRConnected.Returns(false);
        _portalLoad.OnConnectionStateChanged += Raise.Event<Action>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='reconnect-retry-btn']"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='reconnect-retry-btn']").Click());

        // #1838: reconnect must go through ResumeAsync (probe-then-rebuild), never a bare
        // RefreshAsync that trusts a possibly-zombie socket.
        await _portalLoad.Received().ResumeAsync(Arg.Any<CancellationToken>());
        await _portalLoad.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Auto_retry_tick_resumes_while_disconnected()
    {
        _portalLoad.IsSignalRConnected.Returns(true);
        var cut = _ctx.Render<ReconnectOverlay>();
        _portalLoad.IsSignalRConnected.Returns(false);
        _portalLoad.OnConnectionStateChanged += Raise.Event<Action>();

        await cut.InvokeAsync(() => cut.Instance.RetryTickAsync());

        await _portalLoad.Received().ResumeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Auto_retry_tick_is_a_no_op_once_connected()
    {
        _portalLoad.IsSignalRConnected.Returns(true);
        var cut = _ctx.Render<ReconnectOverlay>();

        await cut.InvokeAsync(() => cut.Instance.RetryTickAsync());

        await _portalLoad.DidNotReceive().ResumeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Dispose_unsubscribes_from_connection_state_changes()
    {
        _portalLoad.IsSignalRConnected.Returns(true);
        var cut = _ctx.Render<ReconnectOverlay>();
        cut.Instance.Dispose();

        // Raising after dispose must not throw.
        _portalLoad.OnConnectionStateChanged += Raise.Event<Action>();
    }
}
