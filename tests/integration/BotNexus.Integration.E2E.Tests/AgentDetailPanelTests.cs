using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the AgentDetailPanel — the rich per-agent editor reached via /agents/{id}.
/// Covers: all sections visible, dirty indicator, save/cancel, delete confirmation,
/// field validation, section expand/collapse.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class AgentDetailPanelTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public AgentDetailPanelTests(NewUserExperienceFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    public async Task InitializeAsync()
    {
        await PlaywrightBootstrap.EnsureBrowserInstalledAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await PlaywrightBootstrap.LaunchChromiumAsync(_playwright);
    }

    public async Task DisposeAsync()
    {
        await _browser.CloseAsync();
        _playwright.Dispose();
    }

    private async Task<IPage> NavigateToAgentDetailAsync(string agentId)
    {
        var ctx = await _browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{_fx.GatewayBaseUrl}/agents/{agentId}", new() { Timeout = 30_000 });
        await page.WaitForSelectorAsync(".agent-detail-panel, .portal-loading",
            new() { Timeout = 30_000 });
        // Wait for loading spinner to finish
        try
        {
            await page.WaitForSelectorAsync(".agent-detail-panel .agent-section",
                new() { Timeout = 15_000 });
        }
        catch { /* May not have sections if load failed */ }
        return page;
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "AgentDetailPanel")]
    public async Task AgentDetailPanel_LoadsAgentData_ForFirstAgent()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var agentId = _fx.AgentIds[0];
        var page = await NavigateToAgentDetailAsync(agentId);

        var panel = page.Locator(".agent-detail-panel");
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var idLabel = (await panel.Locator(".agent-detail-id").TextContentAsync() ?? "").Trim();
        _out.WriteLine($"Agent detail ID: {idLabel}");
        Assert.Equal(agentId, idLabel);
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "AgentDetailPanel")]
    public async Task AgentDetailPanel_ShowsAllRequiredSections()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var page = await NavigateToAgentDetailAsync(_fx.AgentIds[0]);

        var sections = page.Locator(".agent-section");
        var count = await sections.CountAsync();
        _out.WriteLine($"Sections found: {count}");
        Assert.True(count >= 8, $"AgentDetailPanel should have at least 8 config sections, got {count}.");

        // Verify key section titles
        var sectionTitles = await page.Locator(".agent-section-title").AllTextContentsAsync();
        var titles = sectionTitles.Select(t => t.Trim()).ToList();
        _out.WriteLine($"Sections: {string.Join(", ", titles)}");

        Assert.True(titles.Any(t => t.Contains("Identity")), "Missing 'Identity' section.");
        Assert.True(titles.Any(t => t.Contains("System Prompt")), "Missing 'System Prompt' section.");
        Assert.True(titles.Any(t => t.Contains("Tools")), "Missing 'Tools' section.");
        Assert.True(titles.Any(t => t.Contains("Memory")), "Missing 'Memory' section.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "AgentDetailPanel")]
    public async Task AgentDetailPanel_DirtyIndicator_ShowsAfterEdit()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var page = await NavigateToAgentDetailAsync(_fx.AgentIds[0]);
        await page.WaitForSelectorAsync(".agent-section", new() { Timeout = 15_000 });

        // Initially no dirty indicator
        var dirtyIndicator = page.Locator(".agents-dirty-indicator");
        var initialCount = await dirtyIndicator.CountAsync();
        Assert.Equal(0, initialCount);

        // Modify Display Name
        var displayNameInput = page.Locator("input.cfg-input").First;
        await displayNameInput.FillAsync("Modified Display Name " + DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Dirty indicator should appear
        await dirtyIndicator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        var dirtyText = (await dirtyIndicator.TextContentAsync() ?? "").Trim();
        _out.WriteLine($"Dirty indicator: {dirtyText}");
        Assert.True(dirtyText.Contains("Unsaved"), "Dirty indicator should say 'Unsaved changes'.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "AgentDetailPanel")]
    public async Task AgentDetailPanel_SaveButton_DisabledWhenClean()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var page = await NavigateToAgentDetailAsync(_fx.AgentIds[0]);
        await page.WaitForSelectorAsync(".agent-section", new() { Timeout = 15_000 });

        var saveBtn = page.Locator(".toolbar-btn.primary", new() { HasText = "Save" }).First;
        await saveBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        var isDisabled = await saveBtn.IsDisabledAsync();
        Assert.True(isDisabled, "Save button should be disabled when no changes have been made.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "AgentDetailPanel")]
    public async Task AgentDetailPanel_BackButton_NavigatesToAgentList()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var page = await NavigateToAgentDetailAsync(_fx.AgentIds[0]);
        await page.WaitForSelectorAsync(".agent-detail-panel", new() { Timeout = 15_000 });

        var backBtn = page.Locator("button", new() { HasText = "Back to list" });
        await backBtn.ClickAsync();

        await page.WaitForURLAsync("**/agents**", new() { Timeout = 10_000 });
        var url = page.Url;
        Assert.True(url.TrimEnd('/').EndsWith("/agents", StringComparison.OrdinalIgnoreCase),
            $"Back should navigate to /agents, got: {url}");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "AgentDetailPanel")]
    public async Task AgentDetailPanel_DeleteButton_ShowsConfirmation()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        // Use a temporary agent ID for delete testing — don't delete provisioned agents
        // Navigate to a known agent's detail to verify the delete UI at minimum
        var page = await NavigateToAgentDetailAsync(_fx.AgentIds[0]);
        await page.WaitForSelectorAsync(".agent-danger-zone", new() { Timeout = 15_000 });

        var deleteBtn = page.Locator(".agent-danger-zone .toolbar-btn.danger").First;
        await deleteBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await deleteBtn.ClickAsync();

        // Confirmation UI should appear
        var confirmBtn = page.Locator(".agent-danger-zone button.danger", new() { HasText = "Yes, Delete" });
        await confirmBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
        var cancelBtn = page.Locator(".agent-danger-zone button", new() { HasText = "Cancel" });
        Assert.True(await cancelBtn.IsVisibleAsync(), "Cancel button should appear in delete confirmation.");

        // Cancel to avoid actually deleting the provisioned agent
        await cancelBtn.ClickAsync();
        await confirmBtn.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 3_000 });
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "AgentDetailPanel")]
    public async Task AgentDetailPanel_SectionToggle_ExpandsAndCollapses()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var page = await NavigateToAgentDetailAsync(_fx.AgentIds[0]);
        await page.WaitForSelectorAsync(".agent-section", new() { Timeout = 15_000 });

        // Find a collapsed section (not open by default)
        var sections = page.Locator("details.agent-section");
        var count = await sections.CountAsync();
        Assert.True(count > 1, "Should have multiple expandable sections.");

        // The second section onwards starts collapsed
        var closedSection = sections.Nth(1);
        var summary = closedSection.Locator("summary");
        var bodyBefore = closedSection.Locator(".agent-section-body");

        // Click to expand
        await summary.ClickAsync();
        await bodyBefore.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });

        // Click again to collapse
        await summary.ClickAsync();
        await bodyBefore.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 3_000 });
    }
}
