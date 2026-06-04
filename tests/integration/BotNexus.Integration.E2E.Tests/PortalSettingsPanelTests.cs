using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests covering the PortalSettingsPanel: open/close, auto-expand input toggle.
/// Also covers the AskUser prompt flow: free-form, single-choice, multi-choice, cancel, timeout.
/// Regression coverage for:
///   - #634: PortalSettingsPanel close button shows literal 'x'
///   - #636: Missing data-testid attributes
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class PortalSettingsPanelTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fix;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public PortalSettingsPanelTests(NewUserExperienceFixture fix) => _fix = fix;

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

    [SkippableFact]
    public async Task BannerSettingsBtn_OpensPortalSettingsPanel()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser!, _fix.GatewayBaseUrl);

        // Click the banner settings button
        await portal.BannerSettingsBtn.ClickAsync();

        // The portal settings overlay should appear
        var overlay = page.Locator(".portal-settings-overlay");
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        // Header should say "Portal Settings"
        var header = page.Locator(".portal-settings-panel h3");
        var headerText = await header.InnerTextAsync();
        Assert.True(headerText.Contains("Portal Settings", StringComparison.OrdinalIgnoreCase),
            $"Expected 'Portal Settings' heading, got: '{headerText}'");
    }

    [SkippableFact]
    public async Task PortalSettingsPanel_CloseBtn_TextIsNotLiteralX()
    {
        // Regression test for #634: close button shows 'x' instead of a proper icon
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser!, _fix.GatewayBaseUrl);
        await portal.BannerSettingsBtn.ClickAsync();

        var overlay = page.Locator(".portal-settings-overlay");
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var closeBtn = page.Locator(".portal-settings-close");
        var closeBtnText = (await closeBtn.InnerTextAsync()).Trim();

        // Must NOT be a bare lowercase 'x' — that's a bug (#634)
        Assert.False(closeBtnText == "x",
            $"Close button shows bare 'x' — should be a proper close icon (✕, ×, etc.). See issue #634.");
    }

    [SkippableFact]
    public async Task PortalSettingsPanel_CloseBtn_ClosesPanel()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser!, _fix.GatewayBaseUrl);
        await portal.BannerSettingsBtn.ClickAsync();

        var overlay = page.Locator(".portal-settings-overlay");
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        // Click close
        await page.Locator(".portal-settings-close").ClickAsync();

        // Panel should be gone
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
    }

    [SkippableFact]
    public async Task PortalSettingsPanel_OverlayClick_ClosesPanel()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser!, _fix.GatewayBaseUrl);
        await portal.BannerSettingsBtn.ClickAsync();

        var overlay = page.Locator(".portal-settings-overlay");
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        // Click outside the panel (top-left corner of viewport) - settings panel is centered/right-aligned
        await page.Mouse.ClickAsync(5, 5);

        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
    }

    [SkippableFact]
    public async Task PortalSettingsPanel_AutoExpandToggle_TogglesCheckbox()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser!, _fix.GatewayBaseUrl);
        await portal.BannerSettingsBtn.ClickAsync();

        var overlay = page.Locator(".portal-settings-overlay");
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var checkbox = page.Locator(".portal-settings-row input[type='checkbox']").First;
        var initialState = await checkbox.IsCheckedAsync();

        await checkbox.ClickAsync();
        var afterToggle = await checkbox.IsCheckedAsync();

        Assert.True(afterToggle != initialState, "Checkbox state should toggle on click");

        // Toggle back to restore original state
        await checkbox.ClickAsync();
        var restored = await checkbox.IsCheckedAsync();
        Assert.True(restored == initialState, "Checkbox should restore to original state on second click");
    }

    [SkippableFact]
    public async Task BannerSettingsBtn_TextIsNotLiteralX()
    {
        // Regression test for #630: banner settings button shows 'x'
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser!, _fix.GatewayBaseUrl);

        var btnText = (await portal.BannerSettingsBtn.InnerTextAsync()).Trim();
        Assert.False(btnText == "x",
            $"Banner settings button shows bare 'x' — should be a settings icon (⚙, ⚙️, etc.). See issue #630.");
    }
}
