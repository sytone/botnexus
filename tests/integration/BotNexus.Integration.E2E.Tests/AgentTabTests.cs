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
///
/// DESIGN NOTE: All tab selectors are scoped to the FIRST agent-panel to avoid
/// strict-mode violations — multiple agents render multiple panels concurrently
/// in the DOM, each with its own copy of the tab strip.
/// aria-selected values are lowercase "true"/"false" (Blazor renders C# bool as lowercase).
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

    // Helper: get the first agent panel (already attached after GotoAgentChat)
    private static ILocator ActivePanel(IPage page) =>
        page.Locator("[data-testid='agent-panel']").First;

    [SkippableFact]
    public async Task ConversationTab_IsActiveByDefault()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var panel = ActivePanel(page);
        var convTab = panel.Locator("[data-tab='conversation']").First;
        await convTab.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        // Blazor renders bool as lowercase "true"/"false"
        var ariaSelected = await convTab.GetAttributeAsync("aria-selected");
        Assert.Equal("true", ariaSelected, StringComparer.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AllFourTabs_AreVisible()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var panel = ActivePanel(page);
        await panel.Locator(".agent-tab-bar").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        var tabs = panel.Locator(".agent-panel-tab");
        var count = await tabs.CountAsync();
        Assert.True(count >= 4,
            $"Expected at least 4 tabs (Conversation, Workspace, Reports, Canvas), got {count}");
    }

    [SkippableFact]
    public async Task WorkspaceTab_Click_ShowsWorkspacePanel()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var panel = ActivePanel(page);
        var workspaceTab = panel.Locator("[data-tab='workspace']").First;
        await workspaceTab.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
        await workspaceTab.ClickAsync();

        var ariaSelected = await workspaceTab.GetAttributeAsync("aria-selected");
        Assert.Equal("true", ariaSelected, StringComparer.OrdinalIgnoreCase);

        var url = page.Url;
        Assert.True(url.Contains("tab=workspace", StringComparison.OrdinalIgnoreCase),
            $"URL should contain 'tab=workspace'. URL: {url}");
    }

    [SkippableFact]
    public async Task CanvasTab_Click_ShowsCanvasPanel()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var panel = ActivePanel(page);
        var canvasTab = panel.Locator("[data-tab='canvas']").First;
        await canvasTab.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
        await canvasTab.ClickAsync();

        var ariaSelected = await canvasTab.GetAttributeAsync("aria-selected");
        Assert.Equal("true", ariaSelected, StringComparer.OrdinalIgnoreCase);

        var url = page.Url;
        Assert.True(url.Contains("tab=canvas", StringComparison.OrdinalIgnoreCase),
            $"URL should contain 'tab=canvas'. URL: {url}");
    }

    [SkippableFact]
    public async Task ReportsTab_Click_ShowsReportsPanel()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var panel = ActivePanel(page);
        var reportsTab = panel.Locator("[data-tab='reports']").First;
        await reportsTab.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
        await reportsTab.ClickAsync();

        var ariaSelected = await reportsTab.GetAttributeAsync("aria-selected");
        Assert.Equal("true", ariaSelected, StringComparer.OrdinalIgnoreCase);

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

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var panel = ActivePanel(page);
        var workspaceTab = panel.Locator("[data-tab='workspace']").First;
        await workspaceTab.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
        await workspaceTab.ClickAsync();

        var urlWithTab = page.Url;
        Assert.True(urlWithTab.Contains("tab=workspace"),
            $"URL should contain tab param after click. Got: {urlWithTab}");

        // Navigate away and back
        await page.GoBackAsync();
        await page.GoForwardAsync();

        await ActivePanel(page).Locator(".agent-tab-bar").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });

        var restoredTab = ActivePanel(page).Locator("[data-tab='workspace']").First;
        var ariaSelected = await restoredTab.GetAttributeAsync("aria-selected");
        Assert.Equal("true", ariaSelected, StringComparer.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ConversationTab_Click_RemovesTabParamFromUrl()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser!, _fix.GatewayBaseUrl, _fix.AgentIds[0]);

        var panel = ActivePanel(page);

        // Switch to workspace first
        await panel.Locator("[data-tab='workspace']").First.ClickAsync();

        // Back to conversation (default tab — should remove param)
        await panel.Locator("[data-tab='conversation']").First.ClickAsync();

        var url = page.Url;
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

        await page.GotoAsync(
            $"{_fix.GatewayBaseUrl}/chat/{agentId}?tab=workspace",
            new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60_000 });

        // Wait for tab strip to render (sidebar may be closed — tab bar is in main canvas)
        var panel = ActivePanel(page);
        await panel.Locator(".agent-tab-bar").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });

        var workspaceTab = panel.Locator("[data-tab='workspace']").First;
        var ariaSelected = await workspaceTab.GetAttributeAsync("aria-selected");
        Assert.Equal("true", ariaSelected, StringComparer.OrdinalIgnoreCase);
    }
}
