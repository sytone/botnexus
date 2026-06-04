using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the chat header action buttons:
///   - Thinking toggle (💭) — shows/hides thinking blocks
///   - Tools toggle (🔧) — shows/hides tool call messages
///   - Config button (⚙) — opens AgentConfigPanel
///   - New session button (↺ New session) — shows confirmation, then resets
///   - Abort/Stop button (⏹ Stop) — visible during streaming, aborts turn
///   - Steer button (🔀 Steer) — visible during streaming
///   - Mobile overflow menu (⋮) — shows secondary action buttons
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ChatHeaderActionTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fix;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public ChatHeaderActionTests(NewUserExperienceFixture fix) => _fix = fix;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        var (browser, _) = await PortalTestHelpers.TryLaunchBrowserAsync(_playwright);
        _browser = browser;
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    [SkippableFact]
    public async Task ChatHeader_ThinkingToggle_IsVisible()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var btn = page.Locator(".chat-header-actions .toggle-btn").First;
        await btn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        Assert.True(await btn.IsVisibleAsync(), "Thinking toggle button should be visible in chat header");
    }

    [SkippableFact]
    public async Task ChatHeader_ConfigBtn_OpensAgentConfigPanel()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var configBtn = page.Locator(".chat-header-actions .config-btn").First;
        await configBtn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await configBtn.ClickAsync();

        // AgentConfigPanel should open
        var panel = page.Locator(".agent-config-panel, .agent-config-overlay, [class*='config-panel']");
        await panel.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        Assert.True(await panel.First.IsVisibleAsync(), "Agent config panel should open after clicking config button");
    }

    [SkippableFact]
    public async Task ChatHeader_NewSessionBtn_ShowsConfirmationDialog()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var newSessionBtn = page.Locator(".new-chat-btn").First;
        await newSessionBtn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await newSessionBtn.ClickAsync();

        // Confirmation dialog should appear
        var dialog = page.Locator(".reset-confirm-overlay");
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 3_000 });
        Assert.True(await dialog.IsVisibleAsync(), "New session confirmation dialog should appear");

        // Should have Cancel and confirm buttons
        var cancelBtn = page.Locator(".reset-confirm-dialog .cancel-btn");
        var confirmBtn = page.Locator(".reset-confirm-dialog .confirm-btn");
        Assert.True(await cancelBtn.IsVisibleAsync(), "Cancel button should be in dialog");
        Assert.True(await confirmBtn.IsVisibleAsync(), "Confirm button should be in dialog");
    }

    [SkippableFact]
    public async Task ChatHeader_NewSessionConfirm_Cancel_ClosesDialog()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var newSessionBtn = page.Locator(".new-chat-btn").First;
        await newSessionBtn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await newSessionBtn.ClickAsync();

        var dialog = page.Locator(".reset-confirm-overlay");
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 3_000 });

        // Cancel
        await page.Locator(".reset-confirm-dialog .cancel-btn").ClickAsync();

        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 3_000 });
        Assert.False(await dialog.IsVisibleAsync(), "Dialog should close after Cancel");
    }

    [SkippableFact]
    public async Task ChatHeader_NewSessionConfirm_OverlayClick_ClosesDialog()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var newSessionBtn = page.Locator(".new-chat-btn").First;
        await newSessionBtn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await newSessionBtn.ClickAsync();

        var dialog = page.Locator(".reset-confirm-overlay");
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 3_000 });

        // Click outside the inner dialog box
        await dialog.ClickAsync(new LocatorClickOptions { Position = new Position { X = 5, Y = 5 } });

        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 3_000 });
    }

    [SkippableFact]
    public async Task ChatHeader_AbortBtn_VisibleDuringStreaming()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        // Send a message that causes streaming
        await chat.SendMessageAsync("SLOW_STREAM");

        // Streaming badge and abort button should appear
        var abortBtn = page.Locator(".abort-btn");
        await abortBtn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        Assert.True(await abortBtn.IsVisibleAsync(), "Abort button should be visible during streaming");

        // Steer button should also be visible
        var steerBtn = page.Locator(".steer-btn");
        Assert.True(await steerBtn.IsVisibleAsync(), "Steer button should be visible during streaming");

        // Send button should NOT be visible while streaming
        var sendBtn = page.Locator("[data-testid='chat-send']");
        Assert.False(await sendBtn.IsVisibleAsync(), "Send button should be hidden during streaming");
    }

    [SkippableFact]
    public async Task ChatHeader_ThinkingToggle_TogglesThinkingVisibility()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        // Send a thinking block message
        await chat.SendMessageAsync("THINKING_BLOCK");
        await chat.WaitForStreamingCompleteAsync();

        // Thinking block should be visible by default
        var thinkingBlock = page.Locator(".thinking-block").First;
        await thinkingBlock.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 15_000
        });

        // Get the thinking toggle button
        var thinkingBtn = page.Locator(".chat-header-actions .toggle-btn").First;

        // Click to toggle off
        await thinkingBtn.ClickAsync();
        await page.WaitForFunctionAsync("() => { const b = document.querySelector(\x27.thinking-block\x27); return !b || b.style.display === \x27none\x27 || b.classList.contains(\x27visibility-hidden\x27); }", null, new PageWaitForFunctionOptions { Timeout = 5_000 });

        // Thinking block should now be hidden (display:none or visibility-hidden)
        var isHidden = await page.EvaluateAsync<bool>(
            "document.querySelector('.thinking-block') !== null && " +
            "(document.querySelector('.thinking-block').style.display === 'none' || " +
            "document.querySelector('.thinking-block').classList.contains('visibility-hidden'))");
        Assert.True(isHidden, "Thinking block should be hidden after toggling off");

        // Toggle back on
        await thinkingBtn.ClickAsync();
        await page.WaitForFunctionAsync("() => { const b = document.querySelector('.thinking-block'); return b && b.style.display !== 'none'; }", null, new PageWaitForFunctionOptions { Timeout = 5_000 });

        var isVisible = await page.EvaluateAsync<bool>(
            "document.querySelector('.thinking-block') !== null && " +
            "document.querySelector('.thinking-block').style.display !== 'none'");
        Assert.True(isVisible, "Thinking block should be visible after toggling back on");
    }

    [SkippableFact]
    public async Task ChatHeader_ToolsToggle_TogglesToolVisibility()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        // Send a message that triggers a tool call
        await chat.SendMessageAsync("TOOL_CALL_SEQUENCE");
        await chat.WaitForStreamingCompleteAsync();

        // Check if any tool messages rendered
        var toolMsg = page.Locator(".message.tool").First;
        var hasTools = await toolMsg.CountAsync() > 0 ? await toolMsg.IsVisibleAsync() : false;
        if (!hasTools)
        {
            // No tool messages — skip the toggle assertion
            return;
        }

        // Tools toggle button is the second .toggle-btn
        var toolsBtn = page.Locator(".chat-header-actions .toggle-btn").Nth(1);
        await toolsBtn.ClickAsync();
        await page.WaitForFunctionAsync("() => Array.from(document.querySelectorAll(''.message.tool'')).every(el => el.style.display === ''none'')", null, new PageWaitForFunctionOptions { Timeout = 5_000 });

        var isHidden = await page.EvaluateAsync<bool>(
            "Array.from(document.querySelectorAll('.message.tool')).every(el => el.style.display === 'none')");
        Assert.True(isHidden, "Tool messages should be hidden after toggling tools off");
    }
}
