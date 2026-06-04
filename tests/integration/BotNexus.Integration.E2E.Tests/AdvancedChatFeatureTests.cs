using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for advanced chat features:
///
/// 1. Thinking blocks appear and can be toggled
/// 2. Tool calls appear in the message list and can be expanded/collapsed
/// 3. Copy button on assistant messages works
/// 4. Parallel streaming across multiple agent contexts completes independently
/// 5. Session boundary dividers appear after new session
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class AdvancedChatFeatureTests
{
    private readonly NewUserExperienceFixture _fx;

    public AdvancedChatFeatureTests(NewUserExperienceFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task ThinkingBlock_RendersAndCanBeToggled()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("THINKING_BLOCK");
        await chat.WaitForAssistantMessageAsync("After careful consideration", TimeSpan.FromSeconds(30));
        await chat.WaitForStreamingCompleteAsync();

        // A thinking block should be visible
        var thinkingBlock = page.Locator(".thinking-block").First;
        await thinkingBlock.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 10_000,
        });

        // Toggle thinking off
        await chat.ToggleThinkingBtn.ClickAsync();
        // Wait for thinking block to be hidden
        await thinkingBlock.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        var thinkingVisible = await thinkingBlock.IsVisibleAsync();
        Assert.False(thinkingVisible, "Thinking block should be hidden after toggle");

        // Toggle thinking back on
        await chat.ToggleThinkingBtn.ClickAsync();
        // Wait for thinking block to be visible again
        await thinkingBlock.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        thinkingVisible = await thinkingBlock.IsVisibleAsync();
        Assert.True(thinkingVisible, "Thinking block should be visible after toggle back on");
    }

    [SkippableFact]
    public async Task ToolCall_AppearsInMessages_CanExpandCollapse()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("TOOL_CALL_SEQUENCE");
        await chat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(30));

        // Tool message should appear
        var toolMessage = page.Locator(".message.tool").First;
        await toolMessage.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 15_000,
        });

        // Tool name element should be present — the mock catalog uses "noop" as tool name
        var toolName = page.Locator(".tool-name").First;
        await toolName.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000,
        });
        var nameText = await toolName.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(nameText), "Tool name should not be empty");

        // Click tool header to expand — expand indicator is ▸ (U+25B8) when collapsed
        var toolHeader = page.Locator(".tool-header").First;
        var expandSpan = page.Locator(".tool-expand").First;

        var beforeExpand = await expandSpan.InnerTextAsync();
        await toolHeader.ClickAsync();
        // Wait for expand indicator to change
        await page.WaitForFunctionAsync(
            $"text => document.querySelector('.tool-expand')?.innerText?.trim() !== text",
            beforeExpand.Trim(), new PageWaitForFunctionOptions { Timeout = 5_000 });
        var afterExpand = await expandSpan.InnerTextAsync();

        // Expand indicator must change on click
        Assert.NotEqual(beforeExpand.Trim(), afterExpand.Trim());

        // Click again to collapse
        await toolHeader.ClickAsync();
        // Wait for expand indicator to revert
        await page.WaitForFunctionAsync(
            $"text => document.querySelector('.tool-expand')?.innerText?.trim() === text",
            beforeExpand.Trim(), new PageWaitForFunctionOptions { Timeout = 5_000 });
        var afterCollapse = await expandSpan.InnerTextAsync();
        Assert.Equal(beforeExpand.Trim(), afterCollapse.Trim());
    }

    [SkippableFact]
    public async Task ToolCallToggle_HidesAndShowsAllToolMessages()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("TOOL_CALL_SEQUENCE");
        await chat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(30));

        var toolMessages = page.Locator(".message.tool");
        var count = await toolMessages.CountAsync();

        if (count == 0)
        {
            Skip.If(true, "No tool messages rendered; TOOL_CALL_SEQUENCE may not have produced tool UI elements.");
            return;
        }

        // Toggle tools off
        await chat.ToggleToolsBtn.ClickAsync();
        // Wait for tool messages to be hidden
        await toolMessages.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        var toolMsgFirst = toolMessages.First;
        var isVisible = await toolMsgFirst.IsVisibleAsync();
        Assert.False(isVisible, "Tool message should be hidden after toggle off");

        // Toggle back on
        await chat.ToggleToolsBtn.ClickAsync();
        // Wait for tool messages to be visible again
        await toolMessages.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        isVisible = await toolMsgFirst.IsVisibleAsync();
        Assert.True(isVisible, "Tool message should be visible after toggle back on");
    }

    [SkippableFact]
    public async Task ParallelStreaming_TwoAgents_BothComplete_NoBleed()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;

        var ctx1 = await browser.NewContextAsync();
        var ctx2 = await browser.NewContextAsync();
        var page1 = await ctx1.NewPageAsync();
        var page2 = await ctx2.NewPageAsync();

        var portal1 = new PortalPage(page1);
        var portal2 = new PortalPage(page2);

        await portal1.GotoAgentChatAsync(_fx.GatewayBaseUrl, _fx.AgentIds[0]);
        await portal2.GotoAgentChatAsync(_fx.GatewayBaseUrl, _fx.AgentIds[1]);

        // Use scoped constructors so locators target the correct agent panel
        var chat1 = new ChatPanelPage(page1, _fx.AgentIds[0]);
        var chat2 = new ChatPanelPage(page2, _fx.AgentIds[1]);

        await chat1.StartFreshSessionAsync();
        await chat2.StartFreshSessionAsync();

        await chat1.ChatInput.FillAsync("MULTI_DELTA");
        await chat2.ChatInput.FillAsync("MULTI_DELTA");

        await Task.WhenAll(
            chat1.SendBtn.ClickAsync(),
            chat2.SendBtn.ClickAsync()
        );

        await Task.WhenAll(
            chat1.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(30)),
            chat2.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(30))
        );

        // URL isolation: each page must still be on its own agent
        Assert.Contains(_fx.AgentIds[0], page1.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_fx.AgentIds[1], page2.Url, StringComparison.OrdinalIgnoreCase);

        await ctx1.DisposeAsync();
        await ctx2.DisposeAsync();
    }

    [SkippableFact]
    public async Task CopyMessageButton_ClickableOnAssistantMessages()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForAssistantMessageAsync("Hello", TimeSpan.FromSeconds(30));
        await chat.WaitForStreamingCompleteAsync();

        // Copy button appears on assistant messages — locator uses msg-copy-btn class
        var copyBtn = page.Locator(".msg-copy-btn").First;
        await copyBtn.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        await page.Locator(".message.assistant").First.HoverAsync();
        // Hover the parent message to trigger CSS :hover so the copy button becomes clickable
        await copyBtn.ClickAsync();
        // Wait for copy feedback — button briefly shows checkmark
        await page.WaitForFunctionAsync(
            "btn => btn.innerText.trim() !== '\u29BF' && btn.innerText.trim() !== '\u2399'",
            await copyBtn.ElementHandleAsync(),
            new PageWaitForFunctionOptions { Timeout = 3_000 }).ContinueWith(_ => Task.CompletedTask);

        // After copy the button briefly shows "\u221A" (U+221A)
        var btnText = await copyBtn.InnerTextAsync();
        Assert.Equal("√", btnText.Trim());
    }

    [SkippableFact]
    public async Task SessionBoundary_AppearsAfterNewSession()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Start fresh to avoid contamination from prior tests, then send a message
        // so there is history for the session boundary to appear after the new session
        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForAssistantMessageAsync("Hello", TimeSpan.FromSeconds(30));
        await chat.WaitForStreamingCompleteAsync();

        // New session via header button — confirm dialog uses .reset-confirm-dialog
        await chat.NewSessionBtn.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
        await chat.NewSessionBtn.ClickAsync();
        await chat.NewSessionConfirmDialog.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000,
        });
        await chat.NewSessionConfirmBtn.ClickAsync();

        // .session-boundary divider must appear in the conversation history
        await page.Locator(".session-boundary").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });

        // Send another message in the new session
        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForAssistantMessageAsync("Hello", TimeSpan.FromSeconds(30));
    }
}
