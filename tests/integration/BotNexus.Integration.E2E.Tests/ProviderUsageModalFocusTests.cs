using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests;

[Collection(NewUserExperienceCollection.Name)]
public sealed class ProviderUsageModalFocusTests
{
    private readonly NewUserExperienceFixture _fixture;

    public ProviderUsageModalFocusTests(NewUserExperienceFixture fixture) => _fixture = fixture;

    [SkippableFact]
    [Trait("Category", "ProviderUsage")]
    public async Task Keyboard_open_traps_focus_escape_closes_and_restores_opener()
    {
        Skip.IfNot(_fixture.Succeeded, $"Fixture failed: {_fixture.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var browserScope = browser!;

        var page = await browser.NewPageAsync();
        await page.GotoAsync(_fixture.GatewayBaseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 30_000,
        });

        var opener = page.GetByTestId("banner-usage-btn");
        await opener.FocusAsync();
        await page.Keyboard.PressAsync("Enter");

        var dialog = page.GetByTestId("provider-usage-panel");
        await dialog.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
        await Assertions.Expect(dialog).ToBeFocusedAsync();

        await page.Keyboard.PressAsync("Tab");
        Assert.True(await dialog.EvaluateAsync<bool>("d => d.contains(document.activeElement)"));

        await page.Keyboard.PressAsync("Shift+Tab");
        Assert.True(await dialog.EvaluateAsync<bool>("d => d.contains(document.activeElement)"));

        await page.Keyboard.PressAsync("Escape");
        await dialog.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = 5_000,
        });
        await Assertions.Expect(opener).ToBeFocusedAsync();
    }
}
