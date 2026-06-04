using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests.PageObjects;

/// <summary>
/// Page object for the chat panel of a single agent.
/// All locators are scoped to the agent's conversation panel element so they
/// work correctly in the multi-panel portal layout (3-4 panels rendered simultaneously).
/// </summary>
public sealed class ChatPanelPage
{
    public IPage Page { get; }

    /// <summary>
    /// Root locator for this agent's conversation panel.
    /// When an agentId is provided the scope is "#agentId-conversation-panel";
    /// otherwise it falls back to the first agent-panel data-testid element.
    /// </summary>
    public ILocator Root { get; }

    // ── Input area ────────────────────────────────────────────────────────
    public ILocator ChatInput       => Root.Locator("[data-testid='chat-input']");
    public ILocator SendBtn         => Root.Locator("[data-testid='chat-send']");
    public ILocator SteerBtn        => Root.Locator(".steer-btn");
    public ILocator AbortBtn        => Root.Locator(".abort-btn");

    // ── Message area ──────────────────────────────────────────────────────
    public ILocator MessagesContainer   => Root.Locator("[data-testid='chat-messages']");
    public ILocator AssistantMessages   => Root.Locator(".message.assistant .message-content, .msg-content");
    public ILocator SystemMessages      => Root.Locator("[data-testid='chat-system-message']");
    public ILocator UserMessages        => Root.Locator(".message.user .message-content");
    public ILocator StreamingIndicator  => Root.Locator(".streaming-indicator");
    public ILocator StreamingBadge      => Root.Locator(".streaming-badge");

    // ── Header area ───────────────────────────────────────────────────────
    public ILocator ConversationTitle   => Root.Locator(".conversation-title");
    public ILocator NewSessionBtn       => Root.Locator(".new-chat-btn");
    public ILocator ConfigBtn           => Root.Locator(".config-btn");
    public ILocator ToggleThinkingBtn   => Root.Locator("button[title='Toggle thinking visibility']");
    public ILocator ToggleToolsBtn      => Root.Locator("button[title='Toggle tool visibility']");

    // ── Command palette ───────────────────────────────────────────────────
    public ILocator CommandPalette      => Root.Locator(".command-palette");
    public ILocator CommandItems        => CommandPalette.Locator(".command-item");

    // ── New session confirm (rendered in body, not scoped to panel) ───────
    public ILocator NewSessionConfirmDialog => Page.Locator(".reset-confirm-dialog").First;
    public ILocator NewSessionConfirmBtn    => Page.Locator(".reset-confirm-dialog .confirm-btn").First;
    public ILocator NewSessionCancelBtn     => Page.Locator(".reset-confirm-dialog .cancel-btn").First;

    /// <summary>Unscoped constructor — falls back to the first visible agent panel.</summary>
    public ChatPanelPage(IPage page)
    {
        Page = page;
        Root = page.Locator("[data-testid='agent-panel']").First;
    }

    /// <summary>Scoped constructor — all locators target the specific agent's panel.</summary>
    public ChatPanelPage(IPage page, string agentId)
    {
        Page = page;
        Root = page.Locator($"#{agentId}-conversation-panel");
    }

    /// <summary>
    /// Click the New Session button, confirm the dialog, then wait for the chat input
    /// to become visible again. Use this at the start of any test that needs a clean
    /// conversation history (prevents cross-test message contamination on the shared gateway).
    /// </summary>
    public async Task StartFreshSessionAsync()
    {
        await NewSessionBtn.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
        await NewSessionBtn.ClickAsync();

        await NewSessionConfirmDialog.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000,
        });
        await NewSessionConfirmBtn.ClickAsync();

        // Wait for the dialog to close and input to be ready
        await NewSessionConfirmDialog.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = 5_000,
        });
        await ChatInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
    }

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
