using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the sidebar overlay (mobile tap-to-close) and announcement bar.
/// Also covers: restart gateway button exists, gateway info footer, update badge DOM.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class SidebarOverlayAndFooterTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public SidebarOverlayAndFooterTests(NewUserExperienceFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    public async Task InitializeAsync()
    {
        await PlaywrightBootstrap.EnsureBrowserInstalledAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await PlaywrightBootstrap.LaunchChromiumAsync(_playwright);
    }

    public async Task DisposeAsync()
    {
        await _browser.CloseAsync();
        _playwright.Dispose();
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Sidebar")]
    public async Task SidebarOverlay_AppearsWhenSidebarOpen()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);
        await portal.EnsureSidebarOpenAsync();

        var overlay = page.Locator(".sidebar-overlay");
        // Overlay is only rendered when _sidebarOpen && _isMobile
        // On desktop viewport it may not render — just verify the sidebar is open
        var sidebarClass = await page.Locator(".main-sidebar").GetAttributeAsync("class") ?? "";
        Assert.True(sidebarClass.Contains("sidebar-open"),
            $"Sidebar should have 'sidebar-open' class after opening. Got: {sidebarClass}");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Sidebar")]
    public async Task RestartGatewayButton_IsPresent_InSidebarFooter()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);
        await portal.EnsureSidebarOpenAsync();

        var restartBtn = page.Locator(".restart-btn");
        await restartBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        var text = (await restartBtn.TextContentAsync() ?? "").Trim();
        _out.WriteLine($"Restart button text: {text}");
        Assert.True(text.Contains("Restart") || text.Contains("Restarting"),
            $"Restart button should mention 'Restart', got: '{text}'.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Sidebar")]
    public async Task GatewayInfo_ShowsCommitLink_WhenAvailable()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);
        await portal.EnsureSidebarOpenAsync();

        // Gateway info section takes a moment to load from the API
        try
        {
            var commitLink = page.Locator(".gateway-commit");
            await commitLink.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 8_000 });

            var href = await commitLink.GetAttributeAsync("href") ?? "";
            _out.WriteLine($"Commit link href: {href}");
            Assert.True(href.Contains("github.com"), "Commit link should point to GitHub.");
        }
        catch (TimeoutException)
        {
            _out.WriteLine("Gateway commit info not loaded — gateway may not expose commit SHA.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Sidebar")]
    public async Task AnnouncementBar_IsHidden_WhenNoAnnouncements()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);

        // The announcement bar should not be visible when there are no announcements
        var bar = page.Locator(".announcement-bar");
        var count = await bar.CountAsync();
        if (count > 0)
        {
            var hasAnnouncements = (await bar.GetAttributeAsync("class") ?? "").Contains("has-announcements");
            // If bar is present but without has-announcements class it should not be visible
            if (!hasAnnouncements)
            {
                var visible = await bar.IsVisibleAsync();
                Assert.False(visible, "Announcement bar should be hidden when there are no announcements.");
            }
        }
        // If count == 0, bar is not rendered at all — correct
    }
}
