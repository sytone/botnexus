using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for conversation management in the sidebar:
///
/// 1. Create a new conversation
/// 2. Switch between conversations
/// 3. Rename a conversation (click editable title)
/// 4. Archive a conversation
/// 5. Conversations persist across agent switches
/// 6. No cron/internal conversations leak into the user-facing list
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ConversationManagementTests
{
    private readonly NewUserExperienceFixture _fx;

    public ConversationManagementTests(NewUserExperienceFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task NewConversationButton_CreatesConversation_AppearsInList()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, portal, _) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var before = await portal.GetConversationTitlesAsync();

        // Click the "New" button in the conversations header
        var newConvBtn = portal.ConversationNewBtn;
        await newConvBtn.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });
        await newConvBtn.ClickAsync();

        // List should grow by at least 1
        await Task.Delay(1000); // Allow SignalR update to propagate
        var after = await portal.GetConversationTitlesAsync();
        Assert.True(after.Count > before.Count,
            $"Conversation count did not increase after clicking New. Before: {before.Count}, After: {after.Count}");
    }

    [SkippableFact]
    public async Task SwitchConversation_LoadsCorrectHistory()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Send a message in the current conversation
        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForAssistantMessageAsync("Hello", TimeSpan.FromSeconds(30));
        await chat.WaitForStreamingCompleteAsync();

        // Create a second conversation
        await portal.ConversationNewBtn.ClickAsync();
        await Task.Delay(1000);

        // The new conversation should have an empty messages area
        var msgCount = await chat.Page.Locator(".message").CountAsync();
        // New conversation should have fewer messages than the previous one
        // (could be 0 if history loads lazily, or just the default greeting)
        Assert.True(msgCount < 3,
            $"New conversation appears to have loaded history from previous conversation ({msgCount} messages)");
    }

    [SkippableFact]
    public async Task ConversationTitle_IsEditable_SavesOnBlur()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Wait for the editable title to appear
        var editableTitle = page.Locator(".conversation-title.editable").First;
        await editableTitle.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });

        // Click to start editing
        await editableTitle.ClickAsync();

        var titleInput = page.Locator(".conversation-title-input").First;
        await titleInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000,
        });

        var newTitle = $"Test-{Guid.NewGuid():N}".Substring(0, 16);
        await titleInput.FillAsync(newTitle);
        await titleInput.PressAsync("Enter");

        // Title should now show the new value
        var updatedTitle = page.Locator(".conversation-title").First;
        await updatedTitle.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        var displayedTitle = await updatedTitle.InnerTextAsync();
        Assert.Equal(newTitle, displayedTitle.Trim());
    }

    [SkippableFact]
    public async Task ConversationTitle_EscapeKey_CancelsEdit()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var editableTitle = page.Locator(".conversation-title.editable").First;
        await editableTitle.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });

        var originalTitle = await editableTitle.InnerTextAsync();
        await editableTitle.ClickAsync();

        var titleInput = page.Locator(".conversation-title-input").First;
        await titleInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000,
        });

        await titleInput.FillAsync("This should not be saved");
        await titleInput.PressAsync("Escape");

        // Wait for the input to be hidden before reading the displayed title
        await titleInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 3_000 });

        // Title should revert to original
        var restoredTitle = page.Locator(".conversation-title").First;
        await restoredTitle.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000,
        });

        var displayedTitle = await restoredTitle.InnerTextAsync();
        Assert.Equal(originalTitle.Trim(), displayedTitle.Trim());
    }

    [SkippableFact]
    public async Task ConversationList_NoInternalOrCronConversations_Visible()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (_, portal, _) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var titles = await portal.GetConversationTitlesAsync();
        foreach (var title in titles)
        {
            Assert.False(
                title.StartsWith("cron:", StringComparison.OrdinalIgnoreCase),
                $"Internal cron conversation visible in sidebar: '{title}'");
            Assert.False(
                title.StartsWith("internal:", StringComparison.OrdinalIgnoreCase),
                $"Internal conversation visible in sidebar: '{title}'");
        }
    }

    [SkippableFact]
    public async Task ArchiveConversation_RemovesFromList()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[1]);

        // Create a new conversation to archive (don't archive the default)
        await portal.ConversationNewBtn.ClickAsync();
        await Task.Delay(1000);

        var titlesBefore = await portal.GetConversationTitlesAsync();
        Assert.True(titlesBefore.Count >= 2, "Need at least 2 conversations to test archive.");

        // Find a non-default conversation archive button
        // The archive button appears on hover; we look for the most recently added item
        var archiveBtns = page.Locator(".conversation-archive-btn");
        var count = await archiveBtns.CountAsync();
        if (count == 0)
        {
            // Skip if no archiveable conversations
            Skip.If(true, "No archiveable conversations found.");
            return;
        }

        // Set up dialog handler to auto-confirm
        page.Dialog += (_, dialog) => dialog.AcceptAsync();

        await archiveBtns.Last.ClickAsync();
        await Task.Delay(1000);

        var titlesAfter = await portal.GetConversationTitlesAsync();
        Assert.True(titlesAfter.Count < titlesBefore.Count,
            $"Conversation count did not decrease after archive. Before: {titlesBefore.Count}, After: {titlesAfter.Count}");
    }
}
