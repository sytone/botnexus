using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for configuration page navigation: all 8 sub-pages render without errors,
/// sidebar sub-nav appears when on the configuration page, and each config section
/// loads its heading/content.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ConfigurationPageTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fix;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public ConfigurationPageTests(NewUserExperienceFixture fix) => _fix = fix;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        var (browser, _) = await PortalTestHelpers.TryLaunchBrowserAsync(_playwright);
        _browser = browser;
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    private async Task<(IPage page, PortalPage portal)> GoToConfigAsync(string? section = null)
    {
        var path = section is null ? "configuration" : $"configuration/{section}";
        var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        var portal = new PortalPage(page);

        await page.GotoAsync($"{_fix.GatewayBaseUrl}/{path}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });

        await page.Locator(".sidebar-nav-item").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000
        });

        return (page, portal);
    }

    [SkippableFact]
    public async Task ConfigurationPage_Loads_WithoutError()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await GoToConfigAsync();

        // Should not show a load error
        var error = page.Locator(".portal-load-error");
        Assert.False(await error.IsVisibleAsync(), "Portal load error should not be visible on configuration page");
    }

    [SkippableFact]
    public async Task ConfigurationPage_SidebarSubNav_IsVisible()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _) = await GoToConfigAsync();

        // Sidebar sub-nav should appear when on the configuration page
        var subnav = page.Locator(".sidebar-subnav");
        await subnav.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        // Should have the expected sub-nav items
        var items = page.Locator(".sidebar-subnav-item");
        var count = await items.CountAsync();
        Assert.True(count >= 6, $"Expected at least 6 config sub-nav items, got {count}");
    }

    [SkippableFact]
    public async Task ConfigurationPage_SidebarSubNav_HiddenOnChatPage()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser!, _fix.GatewayBaseUrl);

        // On chat page, config subnav should not be visible
        var subnav = page.Locator(".sidebar-subnav");
        Assert.False(await subnav.IsVisibleAsync(), "Config subnav should not be visible on the chat page");
    }

    [SkippableFact]
    public async Task ConfigPage_Gateway_LoadsContent()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _) = await GoToConfigAsync("gateway");

        // Should render some config content — check for a known element pattern
        var main = page.Locator(".main-canvas");
        var text = await main.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(text), "Gateway config page should render content");
    }

    [SkippableFact]
    public async Task ConfigPage_Providers_LoadsContent()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _) = await GoToConfigAsync("providers");
        var main = page.Locator(".main-canvas");
        var text = await main.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(text), "Providers config page should render content");
    }

    [SkippableFact]
    public async Task ConfigPage_Channels_LoadsContent()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _) = await GoToConfigAsync("channels");
        var main = page.Locator(".main-canvas");
        var text = await main.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(text), "Channels config page should render content");
    }

    [SkippableFact]
    public async Task ConfigPage_Locations_LoadsContent()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _) = await GoToConfigAsync("locations");
        var main = page.Locator(".main-canvas");
        var text = await main.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(text), "Locations config page should render content");
    }

    [SkippableFact]
    public async Task ConfigPage_World_LoadsContent()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _) = await GoToConfigAsync("world");
        var main = page.Locator(".main-canvas");
        var text = await main.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(text), "World config page should render content");
    }

    [SkippableFact]
    public async Task ConfigPage_Cron_LoadsContent()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _) = await GoToConfigAsync("cron");
        var main = page.Locator(".main-canvas");
        var text = await main.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(text), "Cron config page should render content");
    }

    [SkippableFact]
    public async Task ConfigPage_ApiKey_LoadsContent()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _) = await GoToConfigAsync("apikey");
        var main = page.Locator(".main-canvas");
        var text = await main.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(text), "ApiKey config page should render content");
    }

    [SkippableFact]
    public async Task ConfigPage_Extensions_LoadsContent()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _) = await GoToConfigAsync("extensions");
        var main = page.Locator(".main-canvas");
        var text = await main.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(text), "Extensions config page should render content");
    }

    [SkippableFact]
    public async Task ConfigSubNav_GatewayLink_NavigatesToGateway()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _) = await GoToConfigAsync();

        var gatewayLink = page.Locator(".sidebar-subnav-item").Filter(
            new LocatorFilterOptions { HasTextString = "Gateway" }).First;
        await gatewayLink.ClickAsync();

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.True(page.Url.Contains("configuration/gateway", StringComparison.OrdinalIgnoreCase),
            $"Clicking Gateway sub-nav should navigate to /configuration/gateway. URL: {page.Url}");
    }

    [SkippableFact]
    public async Task ConfigSubNav_AllLinks_ArePresent()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _) = await GoToConfigAsync();

        var subnav = page.Locator(".sidebar-subnav");
        await subnav.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var text = await subnav.InnerTextAsync();
        var expected = new[] { "Gateway", "Providers", "Channels", "Locations", "World", "Cron", "API Key", "Extensions" };

        foreach (var item in expected)
        {
            Assert.True(text.Contains(item, StringComparison.OrdinalIgnoreCase),
                $"Config sub-nav missing expected item: '{item}'. Full nav text: '{text}'");
        }
    }
}
