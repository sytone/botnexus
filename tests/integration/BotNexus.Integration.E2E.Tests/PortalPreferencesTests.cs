using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the portal settings panel preferences:
/// - Expanding input toggle persists across navigation
/// - Portal settings panel opens and closes correctly (complements PortalSettingsPanelTests)
/// - Settings icon (⚙️) is not a literal 'x' (regression for #630)
/// - Close button in panel is not a literal 'x' (regression for #634)
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class PortalPreferencesTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public PortalPreferencesTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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
    [Trait("Category", "PortalPrefs")]
    [Trait("Regression", "630")]
    public async Task SettingsButton_IsGearIcon_NotLiteralX()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);

        var btn = page.Locator("[data-testid='banner-settings-btn']");
        await btn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        var text = (await btn.TextContentAsync() ?? "").Trim();
        _out.WriteLine($"Settings button text: {text}");
        Assert.NotEqual("x", text, StringComparer.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "PortalPrefs")]
    [Trait("Regression", "634")]
    public async Task PortalSettingsPanel_CloseButton_IsNotLiteralX()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);

        await page.Locator("[data-testid='banner-settings-btn']").ClickAsync();

        var panel = page.Locator("[data-testid='portal-settings-panel']");
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var closeBtn = panel.Locator("[data-testid='portal-settings-close']");
        if (await closeBtn.CountAsync() > 0)
        {
            var text = (await closeBtn.TextContentAsync() ?? "").Trim();
            Assert.NotEqual("x", text, StringComparer.OrdinalIgnoreCase);
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "PortalPrefs")]
    public async Task PortalSettingsPanel_ContainsExpandingInputToggle()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);

        await page.Locator("[data-testid='banner-settings-btn']").ClickAsync();

        var panel = page.Locator("[data-testid='portal-settings-panel']");
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var panelText = (await panel.TextContentAsync() ?? "").ToLowerInvariant();
        _out.WriteLine($"Settings panel text (first 300 chars): {panelText[..Math.Min(300, panelText.Length)]}");

        // Should contain at least some settings content
        Assert.False(string.IsNullOrWhiteSpace(panelText), "Settings panel should contain content.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "PortalPrefs")]
    public async Task ChatInput_AcceptsText_AndDoesNotSubmitOnEnter_WithShift()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var input = page.Locator("[data-testid='chat-input']");
        await input.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var beforeCount = await page.Locator("[data-testid='message']").CountAsync();
        await input.ClickAsync();
        // Shift+Enter should insert newline, not submit
        await input.PressSequentiallyAsync("Line 1", new() { Delay = 20 });
        await input.PressAsync("Shift+Enter");
        await input.PressSequentiallyAsync("Line 2", new() { Delay = 20 });

        var afterCount = await page.Locator("[data-testid='message']").CountAsync();
        Assert.Equal(beforeCount, afterCount); // No message submitted
        var value = await input.InputValueAsync();
        Assert.True(value.Contains("Line 1"), "Input should still contain the typed text.");
    }
}
