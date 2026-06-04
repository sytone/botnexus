using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the WorkspacePanel, ReportsPanel, and CanvasPanel.
/// Covers: panel renders without error, workspace file tree structure,
/// canvas renders HTML output, reports panel shows content.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class PanelContentTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fix;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public PanelContentTests(NewUserExperienceFixture fix) => _fix = fix;

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

    private async Task<IPage> GoToAgentTabAsync(string agentId, string tab)
    {
        var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fix.GatewayBaseUrl}/chat/{agentId}?tab={tab}",
            new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60_000 });
        await page.Locator(".agent-tab-bar").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000
        });
        return page;
    }

    // ── Workspace Panel ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task WorkspacePanel_Renders_WithoutError()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var page = await GoToAgentTabAsync(_fix.AgentIds[0], "workspace");

        // Workspace panel section should be active
        var section = page.Locator("#alpha-workspace-panel, [id$='-workspace-panel']").First;
        await section.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 5_000 });

        // Should not show a JS error dialog or crash page
        var errorEl = page.Locator(".portal-load-error");
        Assert.False(await errorEl.IsVisibleAsync(), "Portal load error should not appear on workspace tab");
    }

    [SkippableFact]
    public async Task WorkspacePanel_ShowsFileTreeOrEmptyState()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var page = await GoToAgentTabAsync(_fix.AgentIds[0], "workspace");

        // Scope to the specific agent panel to avoid strict mode violations in multi-panel portal
        // Scope to the workspace tab pane for this agent (not conversation panel)
        var agentPanel = page.Locator($"#{_fix.AgentIds[0]}-workspace-panel");

        // Either a file tree or an empty-state message should render
        var fileTree = agentPanel.Locator(".workspace-file-tree, .workspace-panel");

        // At least one of these should be present
        var treeVisible = await fileTree.IsVisibleAsync();
        Assert.True(treeVisible, "WorkspacePanel should render the workspace-panel section");
    }

    [SkippableFact]
    public async Task WorkspacePanel_LocationsProvisioned_ShowsLocations()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var page = await GoToAgentTabAsync(_fix.AgentIds[0], "workspace");
        await page.WaitForLoadStateAsync(LoadState.Load);

        // The fixture provisions two locations: workspace-tmp and scratch
        // Scope to alpha workspace panel to avoid strict mode violation in multi-panel portal
        var panelContent = await page.Locator("#alpha-workspace-panel").InnerTextAsync();

        // Should contain something — not be completely empty
        Assert.False(string.IsNullOrWhiteSpace(panelContent),
            "Workspace panel with provisioned locations should not be empty");
    }

    // ── Reports Panel ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReportsPanel_Renders_WithoutError()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var page = await GoToAgentTabAsync(_fix.AgentIds[0], "reports");

        var errorEl = page.Locator(".portal-load-error");
        Assert.False(await errorEl.IsVisibleAsync(), "Portal load error should not appear on reports tab");

        // Scope to the specific agent's reports panel
        var activePane = page.Locator($"#{_fix.AgentIds[0]}-reports-panel");
        await activePane.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
    }

    [SkippableFact]
    public async Task ReportsPanel_ShowsContentOrEmptyState()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var page = await GoToAgentTabAsync(_fix.AgentIds[0], "reports");
        await page.WaitForLoadStateAsync(LoadState.Load);

        var activePane = page.Locator($"#{_fix.AgentIds[0]}-reports-panel");
        var paneText = await activePane.InnerTextAsync();
        // Should render something
        Assert.False(string.IsNullOrWhiteSpace(paneText), "Reports panel should render some content");
    }

    // ── Canvas Panel ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CanvasPanel_Renders_WithoutError()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var page = await GoToAgentTabAsync(_fix.AgentIds[0], "canvas");

        var errorEl = page.Locator(".portal-load-error");
        Assert.False(await errorEl.IsVisibleAsync(), "Portal load error should not appear on canvas tab");

        var activePane = page.Locator($"#{_fix.AgentIds[0]}-canvas-panel");
        await activePane.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
    }

    [SkippableFact]
    public async Task CanvasPanel_EmptyCanvas_ShowsPlaceholderOrEmpty()
    {
        // Regression note: issue #628 — no canvas renders a 404.
        // This test verifies the panel renders without a 404 error in the UI.
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        var page = await GoToAgentTabAsync(_fix.AgentIds[0], "canvas");
        await page.WaitForLoadStateAsync(LoadState.Load);

        // Should not show a raw 404 page or crash
        var body = await page.Locator("body").InnerTextAsync();
        Assert.False(body.Contains("404 Not Found", StringComparison.OrdinalIgnoreCase),
            "Canvas panel should not render a raw 404 page. See issue #628.");

        var errorEl = page.Locator(".portal-load-error");
        Assert.False(await errorEl.IsVisibleAsync(), "Portal load error should not appear on canvas tab");
    }

    [SkippableFact]
    public async Task CanvasPanel_TabVisible_ForAllAgents()
    {
        Skip.If(!_fix.Succeeded, _fix.Error ?? "Fixture not ready");
        Skip.If(_browser is null, "Browser unavailable");

        foreach (var agentId in _fix.AgentIds)
        {
            var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(_browser!, _fix.GatewayBaseUrl, agentId);

            // The AgentPanel root div has no id. Locate the agent-panel that contains the
            // #{agentId}-canvas-panel section, then scope the tab to that panel.
            // Use Filter: find agent-panel divs that have a descendant with the canvas-panel id.
            var agentPanelRoot = page.Locator(".agent-panel")
                .Filter(new() { Has = page.Locator($"#{agentId}-canvas-panel") });
            var canvasTab = agentPanelRoot.Locator("[data-tab='canvas']");
            await canvasTab.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

            Assert.True(await canvasTab.IsVisibleAsync(),
                $"Canvas tab should be visible for agent '{agentId}'");
        }
    }
}
