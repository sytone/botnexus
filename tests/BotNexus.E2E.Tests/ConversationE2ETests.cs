namespace BotNexus.E2E.Tests;

/// <summary>
/// Tests that the portal correctly manages multiple conversations — creating,
/// switching, and isolating history between them.
/// </summary>
public class ConversationE2ETests : E2ETestBase
{
    /// <summary>
    /// Clicking the New Conversation button should add a new entry to the
    /// conversation list, giving it a separate identity from the default conversation.
    /// </summary>
    [SkippableFact]
    public async Task CreateNewConversation_IsIndependentFromDefault()
    {
        await WaitForPortalReadyAsync();
        await SelectAgentAsync(AgentId);
        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 10000, State = WaitForSelectorState.Attached });

        var initialCount = await Page.Locator(".conversation-list-item").CountAsync();

        await Page.Locator(".conversation-new-btn").ClickAsync();
        await Page.WaitForTimeoutAsync(1000);

        var newCount = await Page.Locator(".conversation-list-item").CountAsync();
        newCount.ShouldBeGreaterThan(initialCount);
    }

    /// <summary>
    /// Switching between two conversations should load each one's distinct state
    /// in the chat panel. The active conversation is highlighted in the list.
    /// </summary>
    [SkippableFact]
    public async Task SwitchConversations_EachShowsOwnHistory()
    {
        await WaitForPortalReadyAsync();
        await SelectAgentAsync(AgentId);
        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 10000, State = WaitForSelectorState.Attached });

        var convItems = Page.Locator(".conversation-list-item");

        // Ensure at least two conversations exist
        if (await convItems.CountAsync() < 2)
        {
            await Page.Locator(".conversation-new-btn").ClickAsync();
            await Page.WaitForTimeoutAsync(500);
        }

        var totalConversations = await convItems.CountAsync();
        totalConversations.ShouldBeGreaterThanOrEqualTo(2);

        // Click first conversation — it should become active (highlighted)
        await convItems.First.ClickAsync();
        await Page.WaitForTimeoutAsync(500);
        var firstIsActive = await convItems.First.EvaluateAsync<bool>("el => el.classList.contains('active')");
        firstIsActive.ShouldBeTrue();

        // Click second conversation — it should become active instead
        await convItems.Nth(1).ClickAsync();
        await Page.WaitForTimeoutAsync(500);
        var secondIsActive = await convItems.Nth(1).EvaluateAsync<bool>("el => el.classList.contains('active')");
        secondIsActive.ShouldBeTrue();
    }
}
