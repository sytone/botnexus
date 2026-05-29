using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Base helper for tests that need a fully-initialised browser context and
/// the fixture. Handles Playwright install, browser launch, and provides
/// factory methods for page objects.
/// </summary>
public static class PortalTestHelpers
{
    /// <summary>
    /// Ensures Playwright is ready and launches Chromium.
    /// Returns null when browsers are unavailable (caller should skip).
    /// </summary>
    public static async Task<(IBrowser? Browser, string? SkipReason)> TryLaunchBrowserAsync(
        IPlaywright playwright)
    {
        try
        {
            await PlaywrightBootstrap.EnsureBrowserInstalledAsync();
        }
        catch (Exception ex)
        {
            return (null, $"Playwright browser install unavailable: {ex.Message}");
        }

        var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);
        return (browser, null);
    }

    /// <summary>
    /// Open a new page and return both the raw page and a <see cref="PortalPage"/> wrapper.
    /// </summary>
    public static async Task<(IPage page, PortalPage portal)> NewPortalPageAsync(
        IBrowser browser,
        string baseUrl,
        TimeSpan? loadTimeout = null)
    {
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var portal = new PortalPage(page);
        await portal.GotoAndWaitForLoadAsync(baseUrl, loadTimeout);
        return (page, portal);
    }

    /// <summary>
    /// Open a new page navigated directly to a specific agent's chat.
    /// Returns the raw page, portal wrapper and chat panel wrapper.
    /// </summary>
    public static async Task<(IPage page, PortalPage portal, ChatPanelPage chat)> NewChatPageAsync(
        IBrowser browser,
        string baseUrl,
        string agentId,
        TimeSpan? loadTimeout = null)
    {
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var portal = new PortalPage(page);
        var chat = new ChatPanelPage(page);
        await portal.GotoAgentChatAsync(baseUrl, agentId, loadTimeout);
        return (page, portal, chat);
    }

    /// <summary>
    /// Returns a truncated string for use in assertion failure messages.
    /// </summary>
    public static string Truncate(string s, int max = 2000) =>
        s.Length <= max ? s : s[..max] + "…";
}
