using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit tests for the auto-update badge in <see cref="MainLayout"/>.
/// All tests FAIL until Fry implements the badge UI and IUpdateStatusService integration.
/// </summary>
public sealed class UpdateBadgeTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IUpdateStatusService _updateSvc;

    public UpdateBadgeTests()
    {
        // Wire up the same services MainLayoutTests does
        var store = new ClientStateStore();
        var interaction = Substitute.For<IAgentInteractionService>();
        var portalLoad = Substitute.For<IPortalLoadService>();

        portalLoad.IsReady.Returns(true);
        portalLoad.IsLoading.Returns(false);
        portalLoad.LoadError.Returns((string?)null);

        var hub = new GatewayHubConnection();
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var gatewayInfo = new GatewayInfoService(http, restClient);
        var featureFlags = new FeatureFlagsService(_ctx.JSInterop.JSRuntime);

        _updateSvc = Substitute.For<IUpdateStatusService>();

        _ctx.Services.AddSingleton<IClientStateStore>(store);
        _ctx.Services.AddSingleton(interaction);
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(hub);
        _ctx.Services.AddSingleton(gatewayInfo);
        _ctx.Services.AddSingleton(featureFlags);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(_updateSvc);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<MainLayout> RenderLayout() =>
        _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (Microsoft.AspNetCore.Components.RenderFragment)(_ => { })));

    private static UpdateStatus MakeStatus(bool isUpdateAvailable, bool isUpdateInProgress = false,
        string latestShort = "bbbb111") =>
        new(
            Enabled: true,
            IsChecking: false,
            IsUpdateAvailable: isUpdateAvailable,
            IsUpdateInProgress: isUpdateInProgress,
            CurrentCommitSha: "aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000",
            CurrentCommitShort: "aaaa000",
            LatestCommitSha: "bbbb1111bbbb1111bbbb1111bbbb1111bbbb1111",
            LatestCommitShort: latestShort,
            LastCheckedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            NextCheckAt: DateTimeOffset.UtcNow.AddMinutes(55),
            RepositoryOwner: "sytone",
            RepositoryName: "botnexus",
            Branch: "main",
            CompareUrl: null,
            Error: null);

    // ──────────────────────────────────────────────────────────────────
    // Badge visibility
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MainLayout_WhenUpdateNotAvailable_NoBadgeShown()
    {
        _updateSvc.Status.Returns(MakeStatus(isUpdateAvailable: false));

        var cut = RenderLayout();

        Assert.Empty(cut.FindAll(".update-badge"));
    }

    [Fact]
    public void MainLayout_WhenUpdateAvailable_BadgeShown()
    {
        _updateSvc.Status.Returns(MakeStatus(isUpdateAvailable: true));

        var cut = RenderLayout();

        cut.Find(".update-badge");
    }

    [Fact]
    public void MainLayout_UpdateBadge_ShowsLatestCommitShort()
    {
        _updateSvc.Status.Returns(MakeStatus(isUpdateAvailable: true, latestShort: "c0ffee7"));

        var cut = RenderLayout();

        Assert.Contains("c0ffee7", cut.Markup);
    }

    // ──────────────────────────────────────────────────────────────────
    // Confirmation dialog
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MainLayout_UpdateBadge_Click_ShowsConfirmationDialog()
    {
        _updateSvc.Status.Returns(MakeStatus(isUpdateAvailable: true));

        var cut = RenderLayout();

        cut.Find(".update-badge").Click();

        // A confirmation dialog must appear after clicking the badge
        cut.Find(".update-confirm-dialog");
    }

    [Fact]
    public async Task MainLayout_UpdateConfirm_CallsUpdateService()
    {
        _updateSvc.Status.Returns(MakeStatus(isUpdateAvailable: true));
        _updateSvc.StartUpdateAsync(Arg.Any<CancellationToken>()).Returns(202);

        var cut = RenderLayout();
        cut.Find(".update-badge").Click();

        // Click the confirm button inside the dialog
        cut.Find(".update-confirm-btn").Click();

        await _updateSvc.Received(1).StartUpdateAsync(Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────
    // In-progress state
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MainLayout_WhenUpdateInProgress_BadgeShowsUpdating()
    {
        _updateSvc.Status.Returns(MakeStatus(isUpdateAvailable: true, isUpdateInProgress: true));

        var cut = RenderLayout();

        // Badge text / class must indicate updating is in progress
        cut.Find(".update-badge-in-progress");
    }

    [Fact]
    public void MainLayout_WhenUpdateInProgress_UpdateButtonDisabled()
    {
        _updateSvc.Status.Returns(MakeStatus(isUpdateAvailable: true, isUpdateInProgress: true));

        var cut = RenderLayout();
        cut.Find(".update-badge").Click();

        // Confirm button (or the badge itself) must be disabled
        var btn = cut.Find(".update-confirm-btn");
        btn.HasAttribute("disabled").ShouldBeTrue();
    }
}
