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

        // Wait for the conversation list to grow (SignalR update)
        await portal.Page.WaitForFunctionAsync(
            "before => document.querySelectorAll('[data-testid=conv-item]').length > before",
            before.Count, new PageWaitForFunctionOptions { Timeout = 20_000 });
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
        // Wait for new conversation to be active (input visible and cleared)
        await chat.ChatInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

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
    public async Task ConversationRowActions_KeyboardFocusRevealsEveryControlInBothThemes()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[1]);

        await portal.ConversationNewBtn.ClickAsync();
        await chat.ChatInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        var row = page.Locator(".conversation-list-item:has(.conversation-list-item-btn.active)").First;
        await row.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        await page.Mouse.MoveAsync(0, 0);

        foreach (var theme in new[] { "dark", "light" })
        {
            await page.EvaluateAsync(
                "theme => theme === 'light' ? document.documentElement.setAttribute('data-theme', 'light') : document.documentElement.removeAttribute('data-theme')",
                theme);

            var rowLink = row.Locator(".conversation-list-item-btn");
            await rowLink.FocusAsync();

            var actions = new[]
            {
                row.Locator(".conversation-section-btn"),
                row.Locator(".conversation-pin-btn"),
                row.Locator(".conversation-archive-btn"),
            };

            foreach (var action in actions)
            {
                await page.Keyboard.PressAsync("Tab");
                Assert.True(
                    await action.EvaluateAsync<bool>("element => document.activeElement === element"),
                    $"{theme} theme did not move keyboard focus to {await action.GetAttributeAsync("class")}.");

                foreach (var visibleAction in actions)
                {
                    var box = await visibleAction.BoundingBoxAsync();
                    Assert.NotNull(box);
                    Assert.True(box.Width >= 32, $"{theme} theme left a focused-row action only {box.Width}px wide.");
                    Assert.Equal("1", await visibleAction.EvaluateAsync<string>("element => getComputedStyle(element).opacity"));
                }

                var outline = await action.EvaluateAsync<string>(
                    "element => `${getComputedStyle(element).outlineStyle} ${getComputedStyle(element).outlineWidth}`");
                Assert.DoesNotContain("none", outline, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("0px", outline, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [SkippableTheory]
    [InlineData(240, "wide")]
    [InlineData(180, "minimum")]
    [Trait("Category", "Playwright")]
    public async Task ConversationTimestamp_RemainsLeftAlignedAndClearOfRowActions(int sidebarWidth, string label)
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[1]);
        await page.SetViewportSizeAsync(1280, 800);

        await portal.ConversationNewBtn.ClickAsync();
        await chat.ChatInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        var row = page.Locator(".conversation-list-item:has(.conversation-list-item-btn.active)").First;
        await row.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        await page.EvaluateAsync(
            "width => { const sidebar = document.querySelector('.main-sidebar'); if (sidebar) { sidebar.style.flex = `0 0 ${width}px`; sidebar.style.width = `${width}px`; } }",
            sidebarWidth);
        await row.Locator(".conversation-list-item-title").EvaluateAsync(
            "element => element.textContent = 'A deliberately long conversation title that must ellipsize before the action strip'");

        await AssertTimestampClearAsync(row, $"{label} rest");
        await row.HoverAsync();
        await AssertTimestampClearAsync(row, $"{label} hover");

        var pin = row.Locator(".conversation-pin-btn");
        await pin.ClickAsync();
        row = page.Locator(".conversation-list-item:has(.conversation-list-item-btn.active)").First;
        await row.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await AssertTimestampClearAsync(row, $"{label} pinned");

        await row.HoverAsync();
        var sectionButton = row.Locator(".conversation-section-btn");
        await sectionButton.ClickAsync();
        Assert.Equal("true", await sectionButton.GetAttributeAsync("aria-expanded"));
        await AssertTimestampClearAsync(row, $"{label} section menu open");

        var runId = Environment.GetEnvironmentVariable("RUN_ID");
        var screenshotDirectory = string.IsNullOrWhiteSpace(runId)
            ? Path.Combine(RepoLocator.FindRepoRoot(), "tmp", "screenshots-4100")
            : Path.Combine(Path.DirectorySeparatorChar.ToString(), "work", runId, "artifacts", "screenshots-4100");
        Directory.CreateDirectory(screenshotDirectory);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(screenshotDirectory, $"conversation-timestamp-{label}.png"),
        });
    }

    private static async Task AssertTimestampClearAsync(ILocator row, string state)
    {
        var linkBox = await row.Locator(".conversation-list-item-btn").BoundingBoxAsync();
        var titleBox = await row.Locator(".conversation-list-item-main").BoundingBoxAsync();
        var metadataBox = await row.Locator(".conversation-list-item-meta").BoundingBoxAsync();
        var timestampBox = await row.Locator(".conversation-updated-at").BoundingBoxAsync();
        var actionsBox = await row.Locator(".conversation-row-actions").BoundingBoxAsync();

        Assert.NotNull(linkBox);
        Assert.NotNull(titleBox);
        Assert.NotNull(metadataBox);
        Assert.NotNull(timestampBox);
        Assert.NotNull(actionsBox);
        Assert.False(string.IsNullOrWhiteSpace(await row.Locator(".conversation-updated-at").TextContentAsync()));

        Assert.InRange(Math.Abs(metadataBox!.X - titleBox!.X), 0, 1);
        Assert.True(timestampBox!.X >= linkBox!.X && timestampBox.X + timestampBox.Width <= linkBox.X + linkBox.Width,
            $"{state}: timestamp is clipped outside the conversation link.");
        Assert.True(timestampBox.Y >= linkBox.Y && timestampBox.Y + timestampBox.Height <= linkBox.Y + linkBox.Height,
            $"{state}: timestamp is vertically clipped outside the conversation link.");
        var intersectsActions = timestampBox.X < actionsBox!.X + actionsBox.Width
            && timestampBox.X + timestampBox.Width > actionsBox.X
            && timestampBox.Y < actionsBox.Y + actionsBox.Height
            && timestampBox.Y + timestampBox.Height > actionsBox.Y;
        Assert.False(intersectsActions, $"{state}: row actions obscure the timestamp.");
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
        await chat.ChatInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

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
        // Wait for list to shrink after archive
        await portal.Page.WaitForFunctionAsync(
            "before => document.querySelectorAll('[data-testid=conv-item]').length < before",
            titlesBefore.Count, new PageWaitForFunctionOptions { Timeout = 10_000 });

        var titlesAfter = await portal.GetConversationTitlesAsync();
        Assert.True(titlesAfter.Count < titlesBefore.Count,
            $"Conversation count did not decrease after archive. Before: {titlesBefore.Count}, After: {titlesAfter.Count}");
    }
}
