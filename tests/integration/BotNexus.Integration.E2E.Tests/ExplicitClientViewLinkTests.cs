using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>Browser evidence for explicit desktop/mobile client selection (#3597).</summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ExplicitClientViewLinkTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public ExplicitClientViewLinkTests(NewUserExperienceFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
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

    [SkippableFact]
    [Trait("Category", "ExplicitClientView")]
    public async Task Both_clients_render_reciprocal_root_links_with_build_identity()
    {
        Skip.IfNot(_fixture.Succeeded, $"Fixture failed: {_fixture.Error}");
        var screenshotDirectory = ScreenshotDirectory();

        var desktopContext = await _browser.NewContextAsync(new()
        {
            ViewportSize = new ViewportSize { Width = 600, Height = 844 }
        });
        var desktop = await desktopContext.NewPageAsync();
        await desktop.GotoAsync(_fixture.GatewayBaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await desktop.Locator("[data-testid='sidebar-toggle-btn']").ClickAsync();

        var mobileLink = desktop.Locator("[data-testid='mobile-view-link']");
        await mobileLink.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.Equal("/mobile", await mobileLink.GetAttributeAsync("href"));
        await desktop.Locator("[data-testid='client-build-info']").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.DoesNotContain("/mobile", desktop.Url, StringComparison.OrdinalIgnoreCase);
        var desktopShot = Path.Combine(screenshotDirectory, "desktop-narrow-mobile-view-link.png");
        await desktop.ScreenshotAsync(new() { Path = desktopShot, FullPage = true });
        _output.WriteLine($"Screenshot: {desktopShot}");

        var mobileContext = await _browser.NewContextAsync(new()
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            IsMobile = true,
            HasTouch = true
        });
        var mobile = await mobileContext.NewPageAsync();
        await mobile.GotoAsync($"{_fixture.GatewayBaseUrl.TrimEnd('/')}/mobile/", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await mobile.Locator(".overflow-btn").ClickAsync();

        var desktopLink = mobile.Locator("[data-testid='desktop-view-link']");
        await desktopLink.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.Equal("/", await desktopLink.GetAttributeAsync("href"));
        await mobile.Locator("[data-testid='client-build-info']").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        var mobileShot = Path.Combine(screenshotDirectory, "mobile-desktop-view-link.png");
        await mobile.ScreenshotAsync(new() { Path = mobileShot, FullPage = true });
        _output.WriteLine($"Screenshot: {mobileShot}");
    }

    private static string ScreenshotDirectory()
    {
        var runId = Environment.GetEnvironmentVariable("BOTNEXUS_BUILDTEST_RUN_ID");
        var directory = string.IsNullOrWhiteSpace(runId)
            ? Path.Combine(RepoLocator.FindRepoRoot(), "tmp", "screenshots-3597")
            : Path.Combine(Path.DirectorySeparatorChar.ToString(), "work", runId, "artifacts", "screenshots-3597");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
