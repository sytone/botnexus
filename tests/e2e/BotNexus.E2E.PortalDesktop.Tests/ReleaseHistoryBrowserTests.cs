namespace BotNexus.E2E.PortalDesktop.Tests;

public sealed class ReleaseHistoryBrowserTests
{
    [SkippableFact]
    public async Task Home_change_history_renders_canonical_versions_links_and_screenshot()
    {
        var baseUrl = PortalPlaywright.PortalBaseUrl;
        Skip.If(string.IsNullOrWhiteSpace(baseUrl), "E2E_PORTAL_DESKTOP_URL not set; no running desktop portal to drive.");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PortalPlaywright.LaunchChromiumAsync(playwright);
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/releases/release-history.json", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = ManifestJson
        }));

        await page.GotoAsync(baseUrl!);
        await page.Locator("[data-testid='subnav-release-history']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='release-history-entry']", new() { State = WaitForSelectorState.Visible });

        var entries = page.Locator("[data-testid='release-history-entry']");
        (await entries.CountAsync()).ShouldBe(2);
        (await entries.Nth(0).InnerTextAsync()).ShouldContain("2.0.0");
        (await entries.Nth(1).InnerTextAsync()).ShouldContain("1.9.0");
        (await entries.Nth(0).Locator("[data-testid='release-link']").GetAttributeAsync("href"))
            .ShouldBe("https://github.com/Sytone/botnexus/releases/tag/v2.0.0");
        (await entries.Nth(0).Locator("[data-testid='capability-link']").GetAttributeAsync("href"))
            .ShouldBe("https://sytone.github.io/botnexus/user-guide/fictional");

        var screenshotPath = Environment.GetEnvironmentVariable("E2E_RELEASE_HISTORY_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(screenshotPath))
            await page.ScreenshotAsync(new() { Path = screenshotPath, FullPage = true });
    }

    [SkippableFact]
    public async Task Change_history_exposes_loading_empty_and_error_states()
    {
        var baseUrl = PortalPlaywright.PortalBaseUrl;
        Skip.If(string.IsNullOrWhiteSpace(baseUrl), "E2E_PORTAL_DESKTOP_URL not set; no running desktop portal to drive.");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PortalPlaywright.LaunchChromiumAsync(playwright);
        var page = await browser.NewPageAsync();
        var mode = "loading";
        var releaseResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/releases/release-history.json", async route =>
        {
            if (mode == "loading")
                await releaseResponse.Task;

            await route.FulfillAsync(mode is "loading" or "empty"
                ? new() { Status = 200, ContentType = "application/json", Body = EmptyManifestJson }
                : new() { Status = 503, ContentType = "text/plain", Body = "Unavailable" });
        });

        var navigation = page.GotoAsync(baseUrl!.TrimEnd('/') + "/release-history");
        await page.WaitForSelectorAsync("[data-testid='release-history-loading']", new() { State = WaitForSelectorState.Visible });
        releaseResponse.SetResult(true);
        await navigation;
        await page.WaitForSelectorAsync("[data-testid='release-history-empty']", new() { State = WaitForSelectorState.Visible });

        mode = "error";
        await page.ReloadAsync();
        await page.WaitForSelectorAsync("[data-testid='release-history-error']", new() { State = WaitForSelectorState.Visible });
    }

    private const string EmptyManifestJson = "{\"schemaVersion\":\"1.0.0\",\"releases\":[]}";

    private const string ManifestJson = """
        {"schemaVersion":"1.0.0","releases":[
          {"version":"2.0.0","tag":"v2.0.0","commit":"2222222222222222222222222222222222222222","releasedAt":"2026-09-20","releaseUrl":"https://github.com/Sytone/botnexus/releases/tag/v2.0.0","documentationUrl":"https://sytone.github.io/botnexus/releases/v2.0.0/","categories":[{"name":"Features","changes":[{"summary":"Add fictional capability","documentationUrls":["https://sytone.github.io/botnexus/user-guide/fictional"]}]}]},
          {"version":"1.9.0","tag":"v1.9.0","commit":"1111111111111111111111111111111111111111","releasedAt":"2026-09-10","releaseUrl":"https://github.com/Sytone/botnexus/releases/tag/v1.9.0","documentationUrl":"https://sytone.github.io/botnexus/releases/v1.9.0/","categories":[]}
        ]}
        """;
}
