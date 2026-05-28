using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Lazy Playwright loader. The first test that needs a browser triggers an
/// idempotent <c>playwright install chromium</c>; subsequent calls reuse the
/// cached install. If the install fails (e.g. offline CI), the resulting
/// <c>BrowserNotInstalledException</c> is wrapped so dependent tests can skip
/// with <see cref="SkippableFact"/> rather than fail noisily.
/// </summary>
internal static class PlaywrightBootstrap
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static bool _installed;

    public static async Task EnsureBrowserInstalledAsync()
    {
        if (_installed) return;
        await _gate.WaitAsync();
        try
        {
            if (_installed) return;
            // Microsoft.Playwright.Program.Main mirrors the `playwright` cli;
            // exit code 0 means chromium is available afterwards.
            var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            if (exitCode != 0)
                throw new InvalidOperationException($"playwright install chromium exited {exitCode}");
            _installed = true;
        }
        finally
        {
            _gate.Release();
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
