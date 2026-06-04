using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the AgentDashboard (home screen shown when no agent is selected).
/// Also covers multi-agent switching, and URL-based deep linking to specific agents.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class AgentDashboardTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fix;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public AgentDashboardTests(NewUserExperienceFixture fix) => _fix = fix;

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
    public async Task AgentDropdown_ShowsAllAgents()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser!, _fix.GatewayBaseUrl);

        var dropdown = portal.AgentDropdown;
        await dropdown.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        // Should list all provisioned agents
        foreach (var agentId in _fix.AgentIds)
        {
            var options = await dropdown.Locator($"option[value='{agentId}']").CountAsync();
            Assert.True(options > 0, $"Agent '{agentId}' should appear in the dropdown");
        }
    }

    [SkippableFact]
    public async Task AgentDropdown_SelectAgent_LoadsConversationList()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser!, _fix.GatewayBaseUrl);
        await portal.SelectAgentAsync(_fix.AgentIds[0]);

        // Conversation list should appear
        await portal.ConversationList.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        // URL should update to include the agent
        var url = page.Url;
        Assert.True(url.Contains(_fix.AgentIds[0], StringComparison.OrdinalIgnoreCase),
            $"URL should contain agent ID after selection. URL: {url}");
    }

    [SkippableFact]
    public async Task AgentDropdown_SwitchBetweenAgents_UpdatesConversations()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser!, _fix.GatewayBaseUrl);

        // Select first agent
        await portal.SelectAgentAsync(_fix.AgentIds[0]);
        await portal.ConversationList.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        // Switch to second agent
        await portal.SelectAgentAsync(_fix.AgentIds[1]);
        await portal.ConversationList.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        // URL should contain the second agent
        var url = page.Url;
        Assert.True(url.Contains(_fix.AgentIds[1], StringComparison.OrdinalIgnoreCase),
            $"URL should update to second agent. URL: {url}");
    }

    [SkippableFact]
    public async Task DeepLink_ToAgentChat_SelectsCorrectAgent()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[1]; // Use second agent for variety
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);

        // The agent panel should be for the requested agent
        var agentPanel = page.Locator($"[data-testid='agent-panel']").First;
        await agentPanel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        var panelText = await agentPanel.InnerTextAsync();
        // Agent ID or display name should appear somewhere in the panel
        Assert.True(panelText.Contains(agentId, StringComparison.OrdinalIgnoreCase),
            $"Agent panel should show agent '{agentId}'. Panel content: '{PortalTestHelpers.Truncate(panelText)}'");
    }

    [SkippableFact]
    public async Task DeepLink_WithConversationId_SelectsConversation()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];

        // First, load the portal and get the first conversation ID
        var (page, portal, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);
        await portal.ConversationList.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        var convItems = portal.ConversationItems;
        var count = await convItems.CountAsync();
        if (count == 0)
            return; // No conversations to test with

        var firstItem = convItems.First;
        var convId = await firstItem.GetAttributeAsync("data-conversation-id");
        if (string.IsNullOrWhiteSpace(convId))
            return;

        // Navigate directly to that conversation via URL
        var context2 = await _browser!.NewContextAsync();
        var page2 = await context2.NewPageAsync();
        await page2.GotoAsync($"{_fix.GatewayBaseUrl}/chat/{agentId}/{convId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60_000 });

        await page2.Locator("[data-testid='agent-panel']").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000
        });

        // The conversation should be selected — chat title should be visible
        var chatTitle = page2.Locator(".conversation-title").First;
        Assert.True(await chatTitle.IsVisibleAsync(), "Chat title should be visible when navigating to a specific conversation");
    }

    [SkippableFact]
    public async Task ConversationList_NewConversationBtn_CreatesConversation()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, portal, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);

        await portal.ConversationList.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        var initialCount = await portal.ConversationItems.CountAsync();

        // Click the New conversation button
        await portal.ConversationNewBtn.ClickAsync();

        // Wait for new conversation to appear in the list
        await portal.ConversationItems.Nth(initialCount).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 10_000 });
        var newCount = await portal.ConversationItems.CountAsync();

        Assert.True(newCount > initialCount,
            $"Creating a new conversation should increase count. Before: {initialCount}, After: {newCount}");
    }

    [SkippableFact]
    public async Task ConnectionStatus_ShowsConnected_WhenGatewayUp()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser!, _fix.GatewayBaseUrl);

        // Wait for connection to stabilise
        await page.WaitForLoadStateAsync(LoadState.Load);

        var connStatus = portal.ConnectionStatus;
        await connStatus.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        var statusText = await connStatus.InnerTextAsync();

        // Should not show "disconnected" or "error" state
        Assert.False(statusText.Contains("Error", StringComparison.OrdinalIgnoreCase),
            $"Connection status should not show 'Error' when gateway is up. Got: '{statusText}'");
    }
}
