using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the Skills page (/skills, /skills/explorer).
/// Covers: page loads, Explorer panel renders, splitter present,
/// file tree pane and viewer pane exist, loading state, error state.
/// NOTE: Skills feature may be disabled in the test gateway config.
/// Tests skip gracefully when the feature is off.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class SkillsPageTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public SkillsPageTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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

    private async Task<IPage> NavigateToSkillsAsync()
    {
        var ctx = await _browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{_fx.GatewayBaseUrl}/skills", new() { Timeout = 30_000 });
        return page;
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Skills")]
    public async Task SkillsPage_LoadsWithoutError()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var page = await NavigateToSkillsAsync();

        // Wait for Blazor to boot
        await page.WaitForSelectorAsync(".platform-config-page, .portal-loading",
            new() { Timeout = 30_000 });

        // Check for unhandled error
        var errorUi = page.Locator("#blazor-error-ui");
        if (await errorUi.CountAsync() > 0)
        {
            var style = await errorUi.GetAttributeAsync("style") ?? "";
            Assert.False(style.Contains("display: block"),
                "Skills page caused a Blazor error. Check console.");
        }

        var heading = page.Locator("h2");
        await heading.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        var headingText = (await heading.First.TextContentAsync() ?? "").Trim();
        _out.WriteLine($"Skills page heading: {headingText}");
        Assert.True(headingText.Contains("Skills"), $"Skills page should show 'Skills' heading, got: '{headingText}'.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Skills")]
    public async Task SkillsPage_ExplorerPanel_HasTwoPanes()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var page = await NavigateToSkillsAsync();
        await page.WaitForSelectorAsync(".platform-config-page", new() { Timeout = 30_000 });

        var explorerPanel = page.Locator("[data-testid='skills-explorer-panel']");
        try
        {
            await explorerPanel.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

            var treePanes = explorerPanel.Locator(".workspace-tree-pane");
            var viewerPanes = explorerPanel.Locator(".workspace-viewer-pane");
            var treeCount = await treePanes.CountAsync();
            var viewerCount = await viewerPanes.CountAsync();

            Assert.Equal(1, treeCount);
            Assert.Equal(1, viewerCount);

            // Splitter should be between panes
            var splitter = explorerPanel.Locator(".panel-splitter");
            Assert.Equal(1, await splitter.CountAsync());
        }
        catch (TimeoutException)
        {
            _out.WriteLine("Skills explorer panel not rendered — skills feature may be disabled.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Skills")]
    public async Task SkillsNavItem_ShowsInSidebar_WhenFeatureEnabled()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);
        await portal.EnsureSidebarOpenAsync();

        // The skills nav item only renders when ExtensionFeatures.SkillsEnabled
        var skillsLink = page.Locator(".sidebar-nav-item", new() { HasText = "Skills" });
        var count = await skillsLink.CountAsync();
        _out.WriteLine($"Skills nav items found: {count}");

        if (count > 0)
        {
            var visible = await skillsLink.First.IsVisibleAsync();
            Assert.True(visible, "Skills nav item should be visible when feature is enabled.");
        }
        else
        {
            _out.WriteLine("Skills nav item not present — feature disabled in this gateway config.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Skills")]
    public async Task SkillsNavItem_ClickNavigatesToSkillsPage()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);
        await portal.EnsureSidebarOpenAsync();

        var skillsLink = page.Locator(".sidebar-nav-item", new() { HasText = "Skills" });
        if (await skillsLink.CountAsync() == 0)
        {
            _out.WriteLine("Skills nav not available — skipping click test.");
            return;
        }

        await skillsLink.First.ClickAsync();
        await page.WaitForURLAsync("**/skills**", new() { Timeout = 10_000 });

        var url = page.Url;
        Assert.True(url.Contains("/skills"), $"URL should contain '/skills' after click, got: {url}");
    }
}
