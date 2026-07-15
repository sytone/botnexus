using System;
using System.Threading.Tasks;

namespace BotNexus.E2E.PortalMobile.Tests;

/// <summary>
/// Shared Playwright bootstrap for the mobile-portal e2e suite. Mirrors the desktop
/// bootstrap but launches a mobile-emulated context (viewport + touch) so tests
/// exercise the responsive mobile Blazor site.
///
/// <para>Browser install is idempotent; when it cannot be completed (offline CI) the
/// failure surfaces so callers convert it into a <see cref="SkippableFact"/> skip.</para>
/// </summary>
internal static class MobilePlaywright
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _installed;

    /// <summary>
    /// Base URL of a running mobile portal. Unset =&gt; browser tests skip. Set
    /// <c>E2E_PORTAL_MOBILE_URL</c> (e.g. http://localhost:5199) for live coverage.
    /// </summary>
    public static string? PortalBaseUrl =>
        Environment.GetEnvironmentVariable("E2E_PORTAL_MOBILE_URL");

    public static async Task EnsureBrowserInstalledAsync()
    {
        if (_installed)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            if (_installed)
            {
                return;
            }

            var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"playwright install chromium exited {exitCode}");
            }

            _installed = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Launches chromium and returns a mobile-emulated browser context
    /// (iPhone-ish viewport with touch enabled).
    /// </summary>
    public static async Task<IBrowserContext> LaunchMobileContextAsync(IPlaywright playwright)
    {
        await EnsureBrowserInstalledAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });

        return await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            IsMobile = false, // chromium desktop build ignores IsMobile; viewport drives layout
            HasTouch = true,
        });
    }
}
