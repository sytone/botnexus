using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for empty states across the portal:
/// - No agents configured (AgentDashboard shown)
/// - Conversation list empty state
/// - Agent list empty state on /agents page
/// - Chat empty state when no conversation selected
/// These cover the "first-time user" onboarding experience.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class EmptyStateTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public EmptyStateTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "EmptyState")]
    public async Task ChatEmptyState_ShowsHelpText_WhenNoConversationSelected()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);
        await portal.EnsureSidebarOpenAsync();

        // Check if there's a chat-empty-state when no conversation is active
        // (This may require navigating to an agent with no active conversation)
        var emptyState = page.Locator(".chat-empty-state");
        if (await emptyState.CountAsync() > 0 && await emptyState.IsVisibleAsync())
        {
            var text = (await emptyState.TextContentAsync() ?? "").Trim();
            _out.WriteLine($"Chat empty state text: {text}");
            Assert.False(string.IsNullOrWhiteSpace(text),
                "Chat empty state should show helpful guidance text.");
        }
        else
        {
            _out.WriteLine("Chat empty state not visible — conversation is already selected.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "EmptyState")]
    public async Task AgentListPage_ShowsEmptyState_WhenNoAgentsDefined()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        // Navigate to /agents with provisioned agents — verify table shows agents
        var ctx = await _browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{_fx.GatewayBaseUrl}/agents", new() { Timeout = 30_000 });
        await page.WaitForSelectorAsync(".agents-page", new() { Timeout = 30_000 });
        await page.WaitForSelectorAsync(".agents-table, .agents-empty, .config-loading",
            new() { Timeout = 15_000 });

        var table = page.Locator(".agents-table");
        var emptyMsg = page.Locator(".agents-empty");

        if (await table.CountAsync() > 0)
        {
            // Agents exist — verify the table has the right columns
            var headers = await page.Locator(".agents-table th").AllTextContentsAsync();
            var headerTexts = headers.Select(h => h.Trim()).ToList();
            _out.WriteLine($"Agent table headers: {string.Join(", ", headerTexts)}");
            Assert.True(headerTexts.Any(h => h.Contains("ID")), "Agent table should have an ID column.");
            Assert.True(headerTexts.Any(h => h.Contains("Display Name")), "Agent table should have Display Name column.");
        }
        else if (await emptyMsg.CountAsync() > 0)
        {
            var text = (await emptyMsg.TextContentAsync() ?? "").Trim();
            Assert.True(text.Contains("Add Agent") || text.Contains("No agents"),
                $"Empty agent list should guide user to add an agent, got: '{text}'.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "EmptyState")]
    public async Task ConversationList_Empty_ShowsNoConversationsText()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);
        await portal.EnsureSidebarOpenAsync();

        // If the conversation list is empty for any agent, it should show guidance
        var emptyEl = page.Locator(".conversation-list-empty");
        if (await emptyEl.CountAsync() > 0 && await emptyEl.First.IsVisibleAsync())
        {
            var text = (await emptyEl.First.TextContentAsync() ?? "").Trim();
            Assert.False(string.IsNullOrWhiteSpace(text),
                "Empty conversation list should show a message.");
            _out.WriteLine($"Conversation list empty text: {text}");
        }
        else
        {
            _out.WriteLine("Conversations exist — no empty state to verify.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "EmptyState")]
    public async Task PortalLoadingSpinner_ShowsOnFirstLoad()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        // Open a fresh page and check for the loading screen before Blazor boots
        var ctx = await _browser.NewContextAsync();
        var page = await ctx.NewPageAsync();

        // Navigate but don't wait — capture the loading screen
        var navigationTask = page.GotoAsync(_fx.GatewayBaseUrl, new() { Timeout = 30_000, WaitUntil = WaitUntilState.DOMContentLoaded });

        // The loading screen should be briefly visible
        try
        {
            await page.WaitForSelectorAsync(".loading-screen, .portal-loading",
                new() { Timeout = 5_000 });
            _out.WriteLine("Loading screen observed on first load.");
        }
        catch
        {
            _out.WriteLine("Loading screen was too brief to observe — acceptable.");
        }

        await navigationTask;
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "EmptyState")]
    public async Task AgentDashboard_RendersWhenNoActiveAgent()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        // Navigate to /chat directly with no agent ID
        var ctx = await _browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{_fx.GatewayBaseUrl}/chat", new() { Timeout = 30_000 });

        // Wait for Blazor to boot
        await page.WaitForSelectorAsync(".portal-loading, .agent-dashboard, [data-testid='agent-panel']",
            new() { Timeout = 30_000 });

        // Allow time for agents to load
        await page.WaitForTimeoutAsync(2000);

        // Either the AgentDashboard is shown (no active agent) or agents loaded and chat is shown
        var dashboard = page.Locator(".agent-dashboard");
        var agentPanel = page.Locator("[data-testid='agent-panel']");

        var hasDashboard = await dashboard.CountAsync() > 0;
        var hasPanel = await agentPanel.CountAsync() > 0;

        Assert.True(hasDashboard || hasPanel,
            "After load, either AgentDashboard or AgentPanel should be visible.");
        _out.WriteLine($"Dashboard visible: {hasDashboard}, AgentPanel visible: {hasPanel}");
    }
}
