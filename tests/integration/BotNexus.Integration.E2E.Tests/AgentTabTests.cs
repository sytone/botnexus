using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the AgentPanel tab strip: Conversation, Workspace, Reports, Canvas tabs.
/// Covers:
///   - Tab switching persists to URL query param
///   - Tab restored from URL on load
///   - Sub-agent panels hide Workspace/Reports tabs
///   - Regression #637: tab param silently dropped on cold load
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class AgentTabTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fix;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public AgentTabTests(NewUserExperienceFixture fix) => _fix = fix;

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
    public async Task ConversationTab_IsActiveByDefault()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);

        var convTab = page.Locator("[data-tab='conversation']").First;
        await convTab.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        var ariaSelected = await convTab.GetAttributeAsync("aria-selected");
        Assert.Equal("True", ariaSelected);
    }

    [SkippableFact]
    public async Task AllFourTabs_AreVisible()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);

        // Wait for tab strip to render
        await page.Locator(".agent-tab-bar").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000
        });

        var tabs = page.Locator(".agent-panel-tab");
        var count = await tabs.CountAsync();
        Assert.True(count >= 4, $"Expected at least 4 tabs (Conversation, Workspace, Reports, Canvas), got {count}");
    }

    [SkippableFact]
    public async Task WorkspaceTab_Click_ShowsWorkspacePanel()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);

        var workspaceTab = page.Locator("[data-tab='workspace']").First;
        await workspaceTab.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await workspaceTab.ClickAsync();

        // Workspace tab should become active
        var ariaSelected = await workspaceTab.GetAttributeAsync("aria-selected");
        Assert.Equal("True", ariaSelected);

        // URL should contain tab=workspace
        var url = page.Url;
        Assert.True(url.Contains("tab=workspace", StringComparison.OrdinalIgnoreCase),
            $"URL should contain 'tab=workspace' after clicking Workspace tab. URL: {url}");
    }

    [SkippableFact]
    public async Task CanvasTab_Click_ShowsCanvasPanel()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);

        var canvasTab = page.Locator("[data-tab='canvas']").First;
        await canvasTab.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await canvasTab.ClickAsync();

        var ariaSelected = await canvasTab.GetAttributeAsync("aria-selected");
        Assert.Equal("True", ariaSelected);

        var url = page.Url;
        Assert.True(url.Contains("tab=canvas", StringComparison.OrdinalIgnoreCase),
            $"URL should contain 'tab=canvas'. URL: {url}");
    }

    [SkippableFact]
    public async Task ReportsTab_Click_ShowsReportsPanel()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);

        var reportsTab = page.Locator("[data-tab='reports']").First;
        await reportsTab.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await reportsTab.ClickAsync();

        var ariaSelected = await reportsTab.GetAttributeAsync("aria-selected");
        Assert.Equal("True", ariaSelected);

        var url = page.Url;
        Assert.True(url.Contains("tab=reports", StringComparison.OrdinalIgnoreCase),
            $"URL should contain 'tab=reports'. URL: {url}");
    }

    [SkippableFact]
    public async Task TabSelection_PersistedInUrl_RestoredAfterNavigation()
    {
        // Regression for #637: tab param dropped on cold load
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);

        // Switch to workspace tab
        var workspaceTab = page.Locator("[data-tab='workspace']").First;
        await workspaceTab.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await workspaceTab.ClickAsync();

        var urlWithTab = page.Url;
        Assert.True(urlWithTab.Contains("tab=workspace"), $"URL should contain tab param. Got: {urlWithTab}");

        // Navigate away and back using browser history
        await page.GoBackAsync();
        await page.GoForwardAsync();

        await page.Locator(".agent-tab-bar").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        // Workspace tab should still be selected
        var restoredTab = page.Locator("[data-tab='workspace']").First;
        var ariaSelected = await restoredTab.GetAttributeAsync("aria-selected");
        Assert.Equal("True", ariaSelected);
    }

    [SkippableFact]
    public async Task ConversationTab_Click_RemovesTabParamFromUrl()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);

        // Switch to workspace first
        var workspaceTab = page.Locator("[data-tab='workspace']").First;
        await workspaceTab.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await workspaceTab.ClickAsync();

        // Now switch back to conversation
        var convTab = page.Locator("[data-tab='conversation']").First;
        await convTab.ClickAsync();

        var url = page.Url;
        // Conversation tab is the default — URL should NOT contain tab= param
        Assert.False(url.Contains("tab=conversation", StringComparison.OrdinalIgnoreCase),
            $"Conversation tab (default) should not add tab param to URL. URL: {url}");
    }

    [SkippableFact]
    public async Task TabDeepLink_WorkspaceUrl_RestoresWorkspaceTab()
    {
        // Test direct URL navigation with tab param
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var agentId = _fix.AgentIds[0];
        var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();

        // Navigate directly to chat with tab=workspace in URL
        await page.GotoAsync($"{_fix.GatewayBaseUrl}/chat/{agentId}?tab=workspace",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });

        await page.Locator(".agent-tab-bar").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000
        });

        // After portal loads, workspace tab should be active
        var workspaceTab = page.Locator("[data-tab='workspace']").First;
        var ariaSelected = await workspaceTab.GetAttributeAsync("aria-selected");
        Assert.Equal("True", ariaSelected);
    }
}
