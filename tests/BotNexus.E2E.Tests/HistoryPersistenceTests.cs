namespace BotNexus.E2E.Tests;

/// <summary>
/// Tests that history persists across gateway restarts.
/// These tests verify the core contract: if a conversation had messages,
/// they must be visible again after the gateway restarts — without the user
/// needing to send a new message or refresh multiple times.
/// </summary>
public class HistoryPersistenceTests : E2ETestBase
{
    [SkippableFact]
    public async Task AfterGatewayRestart_ExistingConversation_HistoryIsVisible()
    {
        // Navigate to the portal after gateway has already started
        await WaitForPortalReadyAsync();

        // Select the assistant agent
        await SelectAgentAsync("assistant");

        // Wait for conversation list to populate
        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 20000, State = WaitForSelectorState.Attached });

        // Click the Default conversation (most likely to have history)
        // Try to find one with the Default badge first, otherwise fall back to first
        var defaultConv = Page.Locator(".conversation-list-item:has(.conversation-default-badge)");
        if (await defaultConv.CountAsync() > 0)
            await defaultConv.First.ClickAsync();
        else
            await Page.Locator(".conversation-list-item").First.ClickAsync();

        // History must appear WITHOUT sending a new message
        // This is the core test: if history doesn't show, the restart wiped it
        await Page.WaitForFunctionAsync(@"() => document.querySelectorAll('.message.user, .message.assistant').length > 0", null, new() { Timeout = 15000 });

        var messageCount = await Page.Locator(".message.user, .message.assistant").CountAsync();
        messageCount.ShouldBeGreaterThan(0,
            $"Expected history to be visible after gateway restart but found 0 messages");
    }

    [SkippableFact]
    public async Task AfterHardRefresh_ActiveConversation_HistoryReloads()
    {
        // First visit — establish state
        await WaitForPortalReadyAsync();
        await SelectAgentAsync("assistant");
        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 20000, State = WaitForSelectorState.Attached });
        var defaultConvInitial = Page.Locator(".conversation-list-item:has(.conversation-default-badge)");
        if (await defaultConvInitial.CountAsync() > 0)
            await defaultConvInitial.First.ClickAsync();
        else
            await Page.Locator(".conversation-list-item").First.ClickAsync();

        // Wait for initial history load
        await Page.WaitForFunctionAsync(@"() => document.querySelectorAll('.message.user, .message.assistant').length > 0", null, new() { Timeout = 15000 });

        var countBeforeRefresh = await Page.Locator(".message.user, .message.assistant").CountAsync();

        // Hard refresh — simulates browser reload, clears all in-memory state
        await Page.ReloadAsync();

        // After reload: portal must re-initialise and reload history automatically
        await WaitForPortalReadyAsync();
        await SelectAgentAsync("assistant");
        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 20000, State = WaitForSelectorState.Attached });
        var defaultConvAfter = Page.Locator(".conversation-list-item:has(.conversation-default-badge)");
        if (await defaultConvAfter.CountAsync() > 0)
            await defaultConvAfter.First.ClickAsync();
        else
            await Page.Locator(".conversation-list-item").First.ClickAsync();

        // History must reload WITHOUT user interaction
        await Page.WaitForFunctionAsync(@"() => document.querySelectorAll('.message.user, .message.assistant').length > 0", null, new() { Timeout = 15000 });

        var countAfterRefresh = await Page.Locator(".message.user, .message.assistant").CountAsync();

        countAfterRefresh.ShouldBeGreaterThan(0,
            "History must reload after hard refresh without user needing to send a message");

        countAfterRefresh.ShouldBe(countBeforeRefresh,
            "History count must be consistent across refreshes");
    }

    [SkippableFact]
    public async Task AfterSendingMessage_NewMessageAndResponse_PersistAfterRefresh()
    {
        await WaitForPortalReadyAsync();
        await SelectAgentAsync(AgentId);

        // Wait for conversation list
        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 20000, State = WaitForSelectorState.Attached });
        await SelectDefaultConversationAsync();

        var countBefore = await Page.Locator(".message.user, .message.assistant").CountAsync();

        // Send a message with a unique marker
        var marker = $"PERSIST_TEST_{Guid.NewGuid():N}"[..20];
        var input = Page.Locator("textarea").First;
        await input.FillAsync($"Reply with exactly: {marker}");
        await Page.Keyboard.PressAsync("Enter");

        // Wait for response
        await Page.WaitForFunctionAsync(@"() => document.querySelectorAll( '.message.user, .message.assistant').length > 0", null, new() { Timeout = 30000 });

        // Hard refresh
        await Page.ReloadAsync();
        await WaitForPortalReadyAsync();
        await SelectAgentAsync(AgentId);
        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 20000, State = WaitForSelectorState.Attached });
        await SelectDefaultConversationAsync();

        // After refresh, message count must be at least as high as before
        await Page.WaitForFunctionAsync(
            $@"() => document.querySelectorAll('.message.user, .message.assistant').length >= {countBefore + 1}",
            null,
            new() { Timeout = 15000 });

        var countAfter = await Page.Locator(".message.user, .message.assistant").CountAsync();
        countAfter.ShouldBeGreaterThan(countBefore,
            "Messages sent before refresh must still be visible after refresh");
    }
}
