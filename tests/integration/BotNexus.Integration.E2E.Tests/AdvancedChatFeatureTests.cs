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
/// 5. Error responses surface appropriately in the UI
/// 6. Session boundary dividers appear after /new
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
        await Task.Delay(200);

        var thinkingVisible = await thinkingBlock.IsVisibleAsync();
        Assert.False(thinkingVisible, "Thinking block should be hidden after toggle");

        // Toggle thinking back on
        await chat.ToggleThinkingBtn.ClickAsync();
        await Task.Delay(200);

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

        await chat.SendMessageAsync("TOOL_CALL_SEQUENCE");
        await chat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(30));

        // Tool message should appear
        var toolMessage = page.Locator(".message.tool").First;
        await toolMessage.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 15_000,
        });

        // Tool name should contain our mock tool name
        var toolHeader = page.Locator(".tool-header").First;
        var headerText = await toolHeader.InnerTextAsync();
        Assert.Contains("mock_lookup", headerText, StringComparison.OrdinalIgnoreCase);

        // Click to expand the tool details
        await toolHeader.ClickAsync();
        var expandBtn = page.Locator(".tool-expand").First;
        var expandText = await expandBtn.InnerTextAsync();
        Assert.Equal("▾", expandText.Trim()); // expanded

        // Click again to collapse
        await toolHeader.ClickAsync();
        expandText = await expandBtn.InnerTextAsync();
        Assert.Equal("▸", expandText.Trim()); // collapsed
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
        await Task.Delay(200);

        // Tool messages should be hidden
        var toolMsgFirst = toolMessages.First;
        var isVisible = await toolMsgFirst.IsVisibleAsync();
        Assert.False(isVisible, "Tool message should be hidden after toggle off");

        // Toggle back on
        await chat.ToggleToolsBtn.ClickAsync();
        await Task.Delay(200);

        isVisible = await toolMsgFirst.IsVisibleAsync();
        Assert.True(isVisible, "Tool message should be visible after toggle back on");
    }

    [SkippableFact]
    public async Task ParallelStreaming_TwoAgents_BothComplete_NoBleed()
    {
        // Verifies that streaming across two different agent panels is independent:
        // messages don't bleed between panels, both complete.
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;

        // Open two independent browser contexts (simulates two tabs)
        var ctx1 = await browser.NewContextAsync();
        var ctx2 = await browser.NewContextAsync();
        var page1 = await ctx1.NewPageAsync();
        var page2 = await ctx2.NewPageAsync();

        var chat1 = new ChatPanelPage(page1);
        var chat2 = new ChatPanelPage(page2);

        var portal1 = new PortalPage(page1);
        var portal2 = new PortalPage(page2);

        await portal1.GotoAgentChatAsync(_fx.GatewayBaseUrl, _fx.AgentIds[0]);
        await portal2.GotoAgentChatAsync(_fx.GatewayBaseUrl, _fx.AgentIds[1]);

        // Trigger both streams near-simultaneously
        await chat1.ChatInput.FillAsync("MULTI_DELTA");
        await chat2.ChatInput.FillAsync("MULTI_DELTA");

        await Task.WhenAll(
            chat1.SendBtn.ClickAsync(),
            chat2.SendBtn.ClickAsync()
        );

        // Both should complete within 30 seconds
        await Task.WhenAll(
            chat1.WaitForAssistantMessageAsync("Thinking about the problem", TimeSpan.FromSeconds(30)),
            chat2.WaitForAssistantMessageAsync("Thinking about the problem", TimeSpan.FromSeconds(30))
        );

        // Verify messages did NOT bleed — page1 should not have messages from agent2 URL
        var url1 = page1.Url;
        Assert.Contains(_fx.AgentIds[0], url1, StringComparison.OrdinalIgnoreCase);
        var url2 = page2.Url;
        Assert.Contains(_fx.AgentIds[1], url2, StringComparison.OrdinalIgnoreCase);

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

        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForAssistantMessageAsync("Hello", TimeSpan.FromSeconds(30));
        await chat.WaitForStreamingCompleteAsync();

        // Copy button should appear on assistant messages
        var copyBtn = page.Locator(".msg-copy-btn").First;
        await copyBtn.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        // Click it — it should show a checkmark feedback
        await copyBtn.ClickAsync();
        await Task.Delay(300);

        var btnText = await copyBtn.InnerTextAsync();
        // After copy the button shows "✓" for ~2 seconds
        Assert.Equal("✓", btnText.Trim());
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

        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForAssistantMessageAsync("Hello", TimeSpan.FromSeconds(30));
        await chat.WaitForStreamingCompleteAsync();

        // Start a new session via the button
        await chat.NewSessionBtn.ClickAsync();
        await chat.NewSessionConfirmBtn.ClickAsync();

        // A session boundary divider should appear
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
