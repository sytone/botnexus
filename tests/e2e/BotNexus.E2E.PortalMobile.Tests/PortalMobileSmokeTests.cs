using System;
using System.Threading.Tasks;

namespace BotNexus.E2E.PortalMobile.Tests;

/// <summary>
/// Mobile-portal Playwright smoke coverage under <c>tests/e2e/</c> (issue #1962),
/// driven in a mobile-emulated viewport.
///
/// <para><b>Skip contract.</b> When <c>E2E_PORTAL_MOBILE_URL</c> is unset the tests
/// SKIP via <see cref="SkippableFact"/> - never a silent pass. The non-blocking CI
/// job stands up the mobile portal and installs chromium for live coverage.</para>
/// </summary>
public sealed class PortalMobileSmokeTests
{
    [SkippableFact]
    public async Task MobilePortal_Loads_InMobileViewport()
    {
        var baseUrl = MobilePlaywright.PortalBaseUrl;
        Skip.If(
            string.IsNullOrWhiteSpace(baseUrl),
            "E2E_PORTAL_MOBILE_URL not set; no running mobile portal to drive. " +
            "Set it (e.g. http://localhost:5199) to enable this test.");

        using var playwright = await Playwright.CreateAsync();
        await using var context = await MobilePlaywright.LaunchMobileContextAsync(playwright);
        var page = await context.NewPageAsync();

        await page.GotoAsync(baseUrl!);

        // The mobile shell renders a body; assert the document reaches interactive
        // state and the viewport is the mobile width we requested.
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        var width = await page.EvaluateAsync<int>("() => window.innerWidth");
        width.ShouldBeLessThanOrEqualTo(430);
    }
}
