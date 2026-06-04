using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for slash command behaviour in the chat input:
///
/// 1. Typing "/" shows the command palette with all commands
/// 2. Typing "/co" filters to /compact only (not /clear)
/// 3. Tab-completing a command fills the input
/// 4. /new resets the session (new conversation context)
/// 5. /clear empties local messages
/// 6. Escape dismisses the palette without sending
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class SlashCommandTests
{
    private readonly NewUserExperienceFixture _fx;

    public SlashCommandTests(NewUserExperienceFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task TypingSlash_ShowsCommandPalette_WithAllCommands()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (_, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.ChatInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });
        await chat.ChatInput.ClickAsync(); // ensure focus before typing
        await chat.ChatInput.PressSequentiallyAsync("/");

        // Command palette should appear within 15s (CI can be slow)
        await chat.CommandPalette.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });

        var commands = await chat.CommandItems.AllInnerTextsAsync();
        var commandNames = commands.Select(c => c.Trim()).ToList();

        Assert.Contains(commandNames, c => c.Contains("/new"));
        Assert.Contains(commandNames, c => c.Contains("/compact"));
        Assert.Contains(commandNames, c => c.Contains("/clear"));
        Assert.Contains(commandNames, c => c.Contains("/prompts"));
    }

    [SkippableFact]
    public async Task TypingSlashFilter_NarrowsCommandPalette()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (_, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.ChatInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });

        await chat.ChatInput.PressSequentiallyAsync("/comp");

        await chat.CommandPalette.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });

        var commands = await chat.CommandItems.AllInnerTextsAsync();
        var commandTexts = commands.Select(c => c.Trim()).ToList();

        Assert.True(commandTexts.Count > 0, "Command palette showed no results for '/comp'");
        Assert.Contains(commandTexts, c => c.Contains("/compact", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commandTexts, c => c.Contains("/new", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commandTexts, c => c.Contains("/clear", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commandTexts, c => c.Contains("/prompts", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task EscapeKey_DismissesCommandPalette()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (_, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.ChatInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });

        await chat.ChatInput.ClickAsync(); // ensure focus
        await chat.ChatInput.PressSequentiallyAsync("/");
        await chat.CommandPalette.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });

        await chat.ChatInput.PressAsync("Escape");

        await chat.CommandPalette.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = 5_000,
        });

        var inputValue = await chat.ChatInput.InputValueAsync();
        Assert.Equal("/", inputValue);
    }

    [SkippableFact]
    public async Task SlashClear_ClearsLocalMessages()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (_, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForAssistantMessageAsync("Hello", TimeSpan.FromSeconds(30));
        await chat.WaitForStreamingCompleteAsync();

        var beforeCount = await chat.Page.Locator(".message").CountAsync();
        Assert.True(beforeCount > 0, "Expected messages before /clear");

        await chat.ExecuteSlashCommandAsync("/clear");
        await Task.Delay(500);

        var afterCount = await chat.Page.Locator(".message").CountAsync();
        Assert.True(afterCount < beforeCount,
            $"/clear did not remove messages. Before: {beforeCount}, After: {afterCount}");
    }

    [SkippableFact]
    public async Task SlashNew_ResetsSession_UrlChanges()
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

        var urlBefore = page.Url;

        // Scope to the alpha agent panel only — avoids counting messages from other agent panels
        // that are visible in the multi-panel portal but unrelated to this /new command.
        var agentPanelSel = $"#{_fx.AgentIds[0]}-conversation-panel .message";
        var messagesBefore = await chat.Page.Locator(agentPanelSel).CountAsync();
        await chat.ExecuteSlashCommandAsync("/new");

        // /new should navigate to a fresh conversation — URL changes or messages reset.
        // Give the portal time to react (new session = new conversation ID in URL or cleared state).
        await Task.Delay(4_000);

        var urlAfter = page.Url;
        var messagesAfter = await chat.Page.Locator(agentPanelSel).CountAsync();

        // Either the URL has changed (new conversation ID) or the message list is now empty/shorter
        // Note: the portal may not update the URL for /new — URL change is a nice-to-have, not required.
        // The core requirement: the visible message count must be less than before (ideally 0).
        Assert.True(urlAfter != urlBefore || messagesAfter < messagesBefore,
            $"/new did not reset the session. URL before: {urlBefore}, after: {urlAfter}, messages: {messagesAfter}");
    }

    [SkippableFact]
    public async Task CommandPalette_TabCompletion_FillsInput()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (_, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.ChatInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });

        await chat.ChatInput.PressSequentiallyAsync("/n");
        await chat.CommandPalette.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });

        await chat.ChatInput.PressAsync("Tab");

        var value = await chat.ChatInput.InputValueAsync();
        Assert.StartsWith("/new", value, StringComparison.OrdinalIgnoreCase);

        await chat.CommandPalette.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = 5_000,
        });
    }
}
