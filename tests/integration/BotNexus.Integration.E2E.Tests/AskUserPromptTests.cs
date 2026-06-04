using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the AskUser prompt interaction flow.
/// The mock catalog must emit an ASK_USER event sequence for these tests.
/// Covers: free-form submit, single-choice, multi-choice, cancel, timeout.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class AskUserPromptTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fix;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public AskUserPromptTests(NewUserExperienceFixture fix) => _fix = fix;

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

    /// <summary>
    /// Sends a message that triggers ASK_USER and returns the ask-user-prompt locator.
    /// </summary>
    private async Task<(IPage page, ILocator promptEl)> TriggerAskUserAsync(string agentId, string trigger)
    {
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);

        // Ensure a clean slate before sending the AskUser trigger to avoid prior-test
        // contamination leaving the gateway mid-turn (which detaches chat-send in a loop).
        await chat.StartFreshSessionAsync();

        await chat.SendMessageAsync(trigger);

        var promptEl = page.Locator(".ask-user-prompt");
        await promptEl.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 35_000
        });

        return (page, promptEl);
    }

    [SkippableFact]
    public async Task AskUserPrompt_Visible_WhenAgentRequestsInput()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, promptEl) = await TriggerAskUserAsync(agentId, "ASK_USER_FREEFORM");

        // The prompt container should be visible
        Assert.True(await promptEl.IsVisibleAsync(), "AskUser prompt should be visible");

        // Should show the "Agent needs input" header
        var header = page.Locator(".ask-user-title");
        var headerText = await header.InnerTextAsync();
        Assert.True(headerText.Contains("Agent", StringComparison.OrdinalIgnoreCase),
            $"AskUser header should mention 'Agent'. Got: '{headerText}'");

        // The normal chat input should be hidden while prompt is active
        var chatInput = page.Locator("[data-testid='chat-input']");
        Assert.False(await chatInput.IsVisibleAsync(), "Chat input textarea should be hidden while AskUser prompt is active");
    }

    [SkippableFact]
    public async Task AskUserPrompt_FreeForm_CanSubmit()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, promptEl) = await TriggerAskUserAsync(agentId, "ASK_USER_FREEFORM");

        var textarea = page.Locator(".ask-user-free-form").First;
        await textarea.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 3_000 });
        await textarea.FillAsync("My test answer");

        var submitBtn = page.Locator(".ask-user-prompt .send-btn").First;
        await submitBtn.ClickAsync();

        // Prompt should disappear after submit
        await promptEl.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        // Chat input should return
        var chatInput = page.Locator("[data-testid='chat-input']");
        await chatInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        // A system message summarising the answer should appear
        var sysMsg = page.Locator("[data-testid='chat-system-message']").Last;
        var sysMsgText = await sysMsg.InnerTextAsync();
        Assert.True(sysMsgText.Contains("My test answer", StringComparison.OrdinalIgnoreCase),
            $"System message should contain submitted answer. Got: '{sysMsgText}'");
    }

    [SkippableFact]
    public async Task AskUserPrompt_FreeForm_SubmitDisabledWhenEmpty()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, promptEl) = await TriggerAskUserAsync(agentId, "ASK_USER_FREEFORM");

        var textarea = page.Locator(".ask-user-free-form").First;
        await textarea.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 3_000 });
        // Don't fill anything

        var submitBtn = page.Locator(".ask-user-prompt .send-btn").First;
        var isDisabled = await submitBtn.IsDisabledAsync();
        Assert.True(isDisabled, "Submit button should be disabled when free-form input is empty");
    }

    [SkippableFact]
    public async Task AskUserPrompt_Cancel_DismissesPrompt()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, promptEl) = await TriggerAskUserAsync(agentId, "ASK_USER_FREEFORM");

        var cancelBtn = page.Locator(".ask-user-prompt .cancel-btn").First;
        await cancelBtn.ClickAsync();

        // Prompt should disappear
        await promptEl.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        // Chat input should return
        var chatInput = page.Locator("[data-testid='chat-input']");
        await chatInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        // System message should say cancelled
        var sysMsg = page.Locator("[data-testid='chat-system-message']").Last;
        var sysMsgText = await sysMsg.InnerTextAsync();
        Assert.True(sysMsgText.Contains("cancel", StringComparison.OrdinalIgnoreCase),
            $"System message should say cancelled. Got: '{sysMsgText}'");
    }

    [SkippableFact]
    public async Task AskUserPrompt_SingleChoice_SelectAndSubmit()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, promptEl) = await TriggerAskUserAsync(agentId, "ASK_USER_SINGLECHOICE");

        // Radio buttons should be present
        var radios = page.Locator(".ask-user-choice input[type='radio']");
        var radioCount = await radios.CountAsync();
        Assert.True(radioCount > 0, "Single-choice prompt should show radio buttons");

        // Select the first option
        await radios.First.ClickAsync();

        var submitBtn = page.Locator(".ask-user-prompt .send-btn").First;
        Assert.False(await submitBtn.IsDisabledAsync(), "Submit should be enabled after selecting a radio option");
        await submitBtn.ClickAsync();

        await promptEl.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
    }

    [SkippableFact]
    public async Task AskUserPrompt_MultiChoice_SelectMultipleAndSubmit()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, promptEl) = await TriggerAskUserAsync(agentId, "ASK_USER_MULTICHOICE");

        // Checkboxes should be present
        var checkboxes = page.Locator(".ask-user-choice input[type='checkbox']");
        var cbCount = await checkboxes.CountAsync();
        Assert.True(cbCount > 1, "Multi-choice prompt should show multiple checkboxes");

        // Select the first two
        await checkboxes.First.ClickAsync();
        if (cbCount > 1)
            await checkboxes.Nth(1).ClickAsync();

        var submitBtn = page.Locator(".ask-user-prompt .send-btn").First;
        Assert.False(await submitBtn.IsDisabledAsync(), "Submit should be enabled after selecting checkboxes");
        await submitBtn.ClickAsync();

        await promptEl.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
    }
}
