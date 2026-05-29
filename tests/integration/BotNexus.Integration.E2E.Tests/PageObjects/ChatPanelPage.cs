using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests.PageObjects;

/// <summary>
/// Page object for the chat panel of a single agent.
/// </summary>
public sealed class ChatPanelPage
{
    public IPage Page { get; }

    // ── Input area ────────────────────────────────────────────────────────
    public ILocator ChatInput       => Page.Locator("[data-testid='chat-input']").First;
    public ILocator SendBtn         => Page.Locator("[data-testid='chat-send']").First;
    public ILocator SteerBtn        => Page.Locator(".steer-btn").First;
    public ILocator AbortBtn        => Page.Locator(".abort-btn").First;

    // ── Message area ──────────────────────────────────────────────────────
    public ILocator MessagesContainer   => Page.Locator("[data-testid='chat-messages']").First;
    public ILocator AssistantMessages   => Page.Locator(".message.assistant .message-content, .msg-content");
    public ILocator SystemMessages      => Page.Locator("[data-testid='chat-system-message']");
    public ILocator UserMessages        => Page.Locator(".message.user .message-content");
    public ILocator StreamingIndicator  => Page.Locator(".streaming-indicator").First;
    public ILocator StreamingBadge      => Page.Locator(".streaming-badge").First;

    // ── Header area ───────────────────────────────────────────────────────
    public ILocator ConversationTitle   => Page.Locator(".conversation-title").First;
    public ILocator NewSessionBtn       => Page.Locator(".new-chat-btn").First;
    public ILocator ConfigBtn           => Page.Locator(".config-btn").First;
    public ILocator ToggleThinkingBtn   => Page.Locator("button[title='Toggle thinking visibility']").First;
    public ILocator ToggleToolsBtn      => Page.Locator("button[title='Toggle tool visibility']").First;

    // ── Command palette ───────────────────────────────────────────────────
    public ILocator CommandPalette      => Page.Locator(".command-palette").First;
    public ILocator CommandItems        => Page.Locator(".command-item");

    // ── New session confirm ───────────────────────────────────────────────
    public ILocator NewSessionConfirmDialog => Page.Locator(".reset-confirm-dialog").First;
    public ILocator NewSessionConfirmBtn    => Page.Locator(".reset-confirm-dialog .confirm-btn").First;
    public ILocator NewSessionCancelBtn     => Page.Locator(".reset-confirm-dialog .cancel-btn").First;

    public ChatPanelPage(IPage page) => Page = page;

    /// <summary>
    /// Type a message and click Send, then wait for the input to clear
    /// (signal that Blazor processed the send).
    /// </summary>
    public async Task SendMessageAsync(string text)
    {
        await ChatInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });
        await ChatInput.FillAsync(text);

        await SendBtn.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000,
        });
        await SendBtn.ClickAsync();

        // Wait for input to clear — Blazor resets it after dispatching the send
        try
        {
            await Page.WaitForFunctionAsync(
                "() => { var el = document.querySelector(\"[data-testid='chat-input']\"); return el && (el.value || '') === ''; }",
                null,
                new PageWaitForFunctionOptions { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
            // Non-fatal — let the downstream assertions surface the real issue.
        }
    }

    /// <summary>
    /// Type a slash command and wait for the palette, then execute it.
    /// </summary>
    public async Task ExecuteSlashCommandAsync(string command)
    {
        await ChatInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });
        await ChatInput.FillAsync(command);

        // If the command palette is expected, wait for it
        if (command.StartsWith('/') && !command.Contains(' '))
        {
            try
            {
                await CommandPalette.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 3_000,
                });
            }
            catch (TimeoutException)
            {
                // Some commands might not use the palette
            }
        }

        await ChatInput.PressAsync("Enter");
    }

    /// <summary>
    /// Wait for an assistant message containing the given substring to appear.
    /// </summary>
    public async Task WaitForAssistantMessageAsync(string substring, TimeSpan? timeout = null)
    {
        var ms = (float)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
        var locator = AssistantMessages
            .Filter(new LocatorFilterOptions { HasTextString = substring })
            .First;

        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = ms,
            });
        }
        catch (TimeoutException)
        {
            var snapshot = await MessagesContainer.InnerHTMLAsync();
            Xunit.Assert.Fail(
                $"Assistant message containing '{substring}' did not appear within {timeout ?? TimeSpan.FromSeconds(30)}.\n" +
                $"Messages HTML:\n{Truncate(snapshot)}");
        }
    }

    /// <summary>
    /// Wait for a system message (data-testid='chat-system-message') containing the substring.
    /// </summary>
    public async Task WaitForSystemMessageAsync(string substring, TimeSpan? timeout = null)
    {
        var ms = (float)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
        var locator = SystemMessages
            .Filter(new LocatorFilterOptions { HasTextString = substring })
            .First;

        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = ms,
            });
        }
        catch (TimeoutException)
        {
            var snapshot = await MessagesContainer.InnerHTMLAsync();
            Xunit.Assert.Fail(
                $"System message containing '{substring}' did not appear within {timeout ?? TimeSpan.FromSeconds(30)}.\n" +
                $"Messages HTML:\n{Truncate(snapshot)}");
        }
    }

    /// <summary>
    /// Wait until the streaming indicator is gone (turn completed).
    /// </summary>
    public async Task WaitForStreamingCompleteAsync(TimeSpan? timeout = null)
    {
        var ms = (float)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
        // Wait for streaming badge to disappear
        try
        {
            await StreamingBadge.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = ms,
            });
        }
        catch (TimeoutException)
        {
            // Not critical if the badge was never shown
        }
    }

    private static string Truncate(string s, int max = 2000) =>
        s.Length <= max ? s : s[..max] + "…";
}
