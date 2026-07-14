using System;
using System.Threading.Tasks;

namespace BotNexus.E2E.PortalDesktop.Tests;

/// <summary>
/// Shared Playwright bootstrap for the desktop-portal e2e suite.
///
/// <para>The first test that needs a browser triggers an idempotent
/// <c>playwright install chromium</c>. If the install fails (e.g. offline CI with
/// no cached browsers) the failure is surfaced so callers can turn it into a
/// <see cref="SkippableFact"/> skip rather than a hard failure - CI without the
/// browser installed SKIPS cleanly and never silently passes.</para>
/// </summary>
internal static class PortalPlaywright
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _installed;

    /// <summary>
    /// The base URL of a running desktop portal. When unset, browser tests skip:
    /// there is no portal to drive. A CI job or local run that wants live coverage
    /// sets <c>E2E_PORTAL_DESKTOP_URL</c> (e.g. http://localhost:5099).
    /// </summary>
    public static string? PortalBaseUrl =>
        Environment.GetEnvironmentVariable("E2E_PORTAL_DESKTOP_URL");

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

    public static async Task<IBrowser> LaunchChromiumAsync(IPlaywright playwright)
    {
        await EnsureBrowserInstalledAsync();
        return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });
    }
}
