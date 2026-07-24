using System;
using System.Threading.Tasks;

namespace BotNexus.E2E.PortalDesktop.Tests;

/// <summary>
/// Desktop-portal Playwright smoke coverage under the new <c>tests/e2e/</c> category
/// (issue #1962). These tests drive a real browser against a running Blazor Server
/// desktop portal.
///
/// <para><b>Skip contract.</b> When <c>E2E_PORTAL_DESKTOP_URL</c> is not set (default
/// CI / local dev without a live portal) the tests SKIP via <see cref="SkippableFact"/>
/// - they never silently pass. Point that variable at a running portal to get live
/// coverage; the non-blocking CI job (ci-build-test.yml) is responsible for standing
/// the portal up and installing chromium.</para>
/// </summary>
public sealed class PortalDesktopSmokeTests
{
    [SkippableFact]
    public async Task Portal_Loads_And_ShowsSidebar()
    {
        var baseUrl = PortalPlaywright.PortalBaseUrl;
        Skip.If(
            string.IsNullOrWhiteSpace(baseUrl),
            "E2E_PORTAL_DESKTOP_URL not set; no running desktop portal to drive. " +
            "Set it to a live portal (e.g. http://localhost:5099) to enable this test.");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PortalPlaywright.LaunchChromiumAsync(playwright);
        var page = await browser.NewPageAsync();

        await page.GotoAsync(baseUrl!);
        await page.WaitForSelectorAsync(
            ".main-sidebar",
            new() { Timeout = 15000, State = WaitForSelectorState.Attached });

        var sidebar = page.Locator(".main-sidebar");
        (await sidebar.CountAsync()).ShouldBeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Portal_AgentDropdown_HasOptions()
    {
        var baseUrl = PortalPlaywright.PortalBaseUrl;
        Skip.If(
            string.IsNullOrWhiteSpace(baseUrl),
            "E2E_PORTAL_DESKTOP_URL not set; no running desktop portal to drive.");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PortalPlaywright.LaunchChromiumAsync(playwright);
        var page = await browser.NewPageAsync();

        await page.GotoAsync(baseUrl!);
        await page.WaitForSelectorAsync(
            ".agent-dropdown-select",
            new() { Timeout = 15000, State = WaitForSelectorState.Attached });

        var options = await page.Locator(".agent-dropdown-select option[value]").AllTextContentsAsync();
        options.ShouldNotBeEmpty();
    }

    /// <summary>
    /// #2247 acceptance: a deep-link to the canonical route-owned shape
    /// <c>/agent/{agentId}/conversation/{conversationId}</c> must land directly on the right pane
    /// with the composer visible - no intermediate agent-picker click. Optionally set
    /// <c>E2E_PORTAL_DEEPLINK_PATH</c> to a known-good <c>agent/{id}/conversation/{id}</c> path for
    /// the target portal; otherwise the test falls back to the agent-only shape which still exercises
    /// route ownership. Refresh survival is asserted by reloading and re-checking the composer.
    /// </summary>
    [SkippableFact]
    public async Task DeepLink_To_Agent_Conversation_Route_Lands_On_Pane_With_Composer()
    {
        var baseUrl = PortalPlaywright.PortalBaseUrl;
        Skip.If(
            string.IsNullOrWhiteSpace(baseUrl),
            "E2E_PORTAL_DESKTOP_URL not set; no running desktop portal to drive.");

        var deepLinkPath = Environment.GetEnvironmentVariable("E2E_PORTAL_DEEPLINK_PATH");
        if (string.IsNullOrWhiteSpace(deepLinkPath))
            deepLinkPath = "agent/farnsworth";

        var target = baseUrl!.TrimEnd('/') + "/" + deepLinkPath!.TrimStart('/');

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PortalPlaywright.LaunchChromiumAsync(playwright);
        var page = await browser.NewPageAsync();

        // Deep-link straight to the route-owned view (no picker click first).
        await page.GotoAsync(target);

        // The composer (message input) must be visible on the resolved pane.
        await page.WaitForSelectorAsync(
            "[data-testid=\"chat-input\"], .chat-input",
            new() { Timeout = 15000, State = WaitForSelectorState.Visible });
        (await page.Locator("[data-testid=\"chat-input\"], .chat-input").CountAsync())
            .ShouldBeGreaterThan(0);

        // Refresh survival (#2247 acceptance): reloading the same URL restores the same view+composer.
        await page.ReloadAsync();
        await page.WaitForSelectorAsync(
            "[data-testid=\"chat-input\"], .chat-input",
            new() { Timeout = 15000, State = WaitForSelectorState.Visible });
        page.Url.ShouldContain(deepLinkPath!.TrimStart('/'));
    }
}
