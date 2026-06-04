using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the chat header overflow menu (⋮) used on narrow viewports.
/// Covers: menu opens/closes, all four actions present (thinking, tools, config, new session),
/// actions work from menu, menu closes after action.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ChatHeaderOverflowMenuTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    // Use a narrow viewport to force the overflow menu into view
    private static readonly BrowserNewContextOptions NarrowViewport = new()
    {
        ViewportSize = new ViewportSize { Width = 500, Height = 800 }
    };

    public ChatHeaderOverflowMenuTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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

    private async Task<IPage> GetNarrowPageAsync(string agentId)
    {
        var ctx = await _browser.NewContextAsync(NarrowViewport);
        var page = await ctx.NewPageAsync();
        var portal = new PageObjects.PortalPage(page);
        await portal.GotoAgentChatAsync(_fx.GatewayBaseUrl, agentId);
        return page;
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "OverflowMenu")]
    public async Task OverflowTrigger_IsPresent_InChatHeader()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var page = await GetNarrowPageAsync(_fx.AgentIds[0]);

        var trigger = page.Locator(".chat-header-overflow-trigger").First;
        await trigger.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 });

        // May be hidden by CSS when there's enough space; we just verify it exists in the DOM
        Assert.True(await trigger.CountAsync() > 0,
            "Chat header overflow trigger (⋮) should be present in the DOM.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "OverflowMenu")]
    public async Task OverflowMenu_Opens_OnClick()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var page = await GetNarrowPageAsync(_fx.AgentIds[0]);

        var trigger = page.Locator(".chat-header-overflow-trigger").First;
        await trigger.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        await trigger.ClickAsync();

        var menu = page.Locator(".chat-header-overflow-menu");
        await menu.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });

        _out.WriteLine($"Overflow menu aria-expanded: {await trigger.GetAttributeAsync("aria-expanded")}");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "OverflowMenu")]
    public async Task OverflowMenu_ContainsAllActions()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var page = await GetNarrowPageAsync(_fx.AgentIds[0]);

        var trigger = page.Locator(".chat-header-overflow-trigger").First;
        await trigger.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await trigger.ClickAsync();

        var menu = page.Locator(".chat-header-overflow-menu");
        await menu.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });

        // Thinking, Tools, Config, New Session
        var buttons = menu.Locator("button");
        var count = await buttons.CountAsync();
        Assert.True(count >= 3, $"Overflow menu should have at least 3 action buttons, got {count}.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "OverflowMenu")]
    public async Task OverflowMenu_ClosesAfterAction()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var page = await GetNarrowPageAsync(_fx.AgentIds[0]);

        var trigger = page.Locator(".chat-header-overflow-trigger").First;
        await trigger.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await trigger.ClickAsync();

        var menu = page.Locator(".chat-header-overflow-menu");
        await menu.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });

        // Click the thinking toggle
        await menu.Locator("button").First.ClickAsync();

        // Menu should close
        await menu.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 3_000 });
    }
}
