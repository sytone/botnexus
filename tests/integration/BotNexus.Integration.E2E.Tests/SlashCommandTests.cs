using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for slash command behaviour in the chat input:
///
/// 1. Typing "/" shows the command palette with all commands
/// 2. Typing "/com" filters to matching commands
/// 3. Tab-completing a command fills the input
/// 4. /new resets the session
/// 5. /clear empties local messages
/// 6. /compact triggers compaction (covered more deeply in CompactionFlowTests)
/// 7. Escape dismisses the palette without sending
/// 8. Enter executes the selected command
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
        await chat.ChatInput.PressSequentiallyAsync("/");

        // Command palette should appear
        await chat.CommandPalette.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000,
        });

        var commands = await chat.CommandItems.AllInnerTextsAsync();
        var commandNames = commands.Select(c => c.Trim()).ToList();

        // All four commands should be present
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

        await chat.ChatInput.PressSequentiallyAsync("/co");

        await chat.CommandPalette.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000,
        });

        var commands = await chat.CommandItems.AllInnerTextsAsync();
        // "/co" should match /compact but not /new, /clear, /prompts
        Assert.True(commands.Count == 1 || commands.All(c => c.Contains("/co")),
            $"Expected only /compact-matching commands, got: {string.Join(", ", commands)}");
        Assert.Contains(commands, c => c.Contains("/compact"));
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

        await chat.ChatInput.PressSequentiallyAsync("/");
        await chat.CommandPalette.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000,
        });

        await chat.ChatInput.PressAsync("Escape");

        await chat.CommandPalette.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = 5_000,
        });

        // Input text should still be there (not cleared)
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

        // Seed with a message
        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForAssistantMessageAsync("Hello", TimeSpan.FromSeconds(30));
        await chat.WaitForStreamingCompleteAsync();

        var beforeCount = await chat.Page.Locator(".message").CountAsync();
        Assert.True(beforeCount > 0, "Expected messages before /clear");

        // Execute /clear
        await chat.ExecuteSlashCommandAsync("/clear");
        await Task.Delay(500); // Allow Blazor to re-render

        var afterCount = await chat.Page.Locator(".message").CountAsync();
        Assert.True(afterCount < beforeCount,
            $"/clear did not remove messages. Before: {beforeCount}, After: {afterCount}");
    }

    [SkippableFact]
    public async Task SlashNew_ResetsSession_MessagesCleared()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (_, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Seed with a message
        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForAssistantMessageAsync("Hello", TimeSpan.FromSeconds(30));
        await chat.WaitForStreamingCompleteAsync();

        // Execute /new
        await chat.ExecuteSlashCommandAsync("/new");

        // New session should result in fewer messages (ideally 0, possibly a session boundary marker)
        await Task.Delay(1000);
        var sessionBoundaries = await chat.Page.Locator(".session-boundary").CountAsync();
        // After /new there should be a session boundary divider in the conversation history
        // OR the message list is empty for the new session context
        var totalMessages = await chat.Page.Locator(".message").CountAsync();

        // Either way the new session is clean — assert we have a boundary OR empty messages
        Assert.True(sessionBoundaries > 0 || totalMessages == 0,
            $"After /new, expected session boundary or empty messages. Boundaries: {sessionBoundaries}, Messages: {totalMessages}");
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
            Timeout = 5_000,
        });

        await chat.ChatInput.PressAsync("Tab");

        // After Tab the input should be filled with the completed command + space
        var value = await chat.ChatInput.InputValueAsync();
        Assert.StartsWith("/new", value, StringComparison.OrdinalIgnoreCase);

        // Palette should be dismissed
        await chat.CommandPalette.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = 3_000,
        });
    }
}
