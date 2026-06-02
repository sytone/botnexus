using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests.PageObjects;

/// <summary>
/// Page object for the mobile Blazor client at /mobile/.
/// Mirrors the mobile Chat.razor structure.
/// </summary>
public class MobilePortalPage
{
    private readonly IPage _page;

    public MobilePortalPage(IPage page)
    {
        _page = page;
    }

    public ILocator AgentSelect => _page.Locator(".agent-select");
    public ILocator ConvSelect => _page.Locator(".conv-select");
    public ILocator MessageStream => _page.Locator(".message-stream");
    public ILocator InputTextarea => _page.Locator(".input-textarea");
    public ILocator SendButton => _page.Locator(".send-btn");
    public ILocator OverflowButton => _page.Locator(".overflow-btn");
    public ILocator OverflowDropdown => _page.Locator(".overflow-dropdown");
    public ILocator TopBar => _page.Locator(".top-bar");
    public ILocator BottomBar => _page.Locator(".bottom-bar");
    public ILocator ErrorUi => _page.Locator("#blazor-error-ui");
    public ILocator StreamCursor => _page.Locator(".stream-cursor");
    public ILocator LoadingSpinner => _page.Locator(".portal-load-spinner");

    /// <summary>
    /// Navigates to the mobile portal URL.
    /// </summary>
    public async Task NavigateAsync(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/') + "/mobile/";
        await _page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30_000
        });
    }

    /// <summary>
    /// Waits until the portal is ready (loading spinner gone, agent select visible).
    /// </summary>
    public async Task WaitForReadyAsync(int timeoutMs = 20_000)
    {
        // Wait for loading spinner to disappear
        try
        {
            await LoadingSpinner.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Detached,
                Timeout = timeoutMs
            });
        }
        catch { /* spinner may never appear if load is fast */ }

        // Wait for agent select to appear and be populated
        await AgentSelect.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeoutMs
        });
    }

    /// <summary>
    /// Returns true when the message stream is scrolled to (or near) the bottom.
    /// </summary>
    public async Task<bool> IsScrolledToBottomAsync(int thresholdPx = 50)
    {
        return await _page.EvaluateAsync<bool>($@"() => {{
            const el = document.querySelector('.message-stream');
            if (!el) return false;
            return el.scrollHeight - el.scrollTop - el.clientHeight < {thresholdPx};
        }}");
    }

    /// <summary>
    /// Returns the current scrollTop of the message stream.
    /// </summary>
    public async Task<double> GetScrollTopAsync()
    {
        return await _page.EvaluateAsync<double>(
            "() => document.querySelector('.message-stream')?.scrollTop ?? 0");
    }

    /// <summary>
    /// Force-scrolls the message stream to top to simulate reading history.
    /// </summary>
    public async Task ScrollToTopAsync()
    {
        await _page.EvaluateAsync(
            "() => { const el = document.querySelector('.message-stream'); if (el) el.scrollTop = 0; }");
    }

    /// <summary>
    /// Sends a message via the text input and send button.
    /// </summary>
    public async Task SendMessageAsync(string text)
    {
        await InputTextarea.FillAsync(text);
        await SendButton.ClickAsync();
    }

    /// <summary>
    /// Waits for any active streaming to complete (stream cursor disappears).
    /// </summary>
    public async Task WaitForStreamingCompleteAsync(int timeoutMs = 30_000)
    {
        await StreamCursor.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Detached,
            Timeout = timeoutMs
        });
    }
}
