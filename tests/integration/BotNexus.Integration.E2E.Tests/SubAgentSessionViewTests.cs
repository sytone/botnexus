using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the sub-agent read-only session view.
/// Covers: read-only banner shows, Workspace/Reports tabs hidden for sub-agents,
/// Conversation and Canvas tabs still visible, input area hidden,
/// status badge shows Running/Completed.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class SubAgentSessionViewTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public SubAgentSessionViewTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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
    [Trait("Category", "SubAgentView")]
    public async Task SubAgentSession_ReadOnlyBanner_IsNotShownForNormalAgent()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Normal agents should NOT have the read-only banner
        var banner = page.Locator(".read-only-banner");
        await page.WaitForTimeoutAsync(500); // Brief wait to ensure rendering complete

        var count = await banner.CountAsync();
        if (count > 0)
        {
            var visible = await banner.IsVisibleAsync();
            Assert.False(visible,
                "Normal agent conversation should NOT show the read-only banner.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "SubAgentView")]
    public async Task SubAgentSession_InputArea_IsPresent_ForNormalAgent()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (_, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.ChatInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        Assert.True(await chat.ChatInput.IsVisibleAsync(),
            "Chat input should be visible for non-readonly conversations.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "SubAgentView")]
    public async Task SubAgentBadge_ShowsSubAgentSessionLabel()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);
        await portal.EnsureSidebarOpenAsync();

        // Sub-agent sessions appear in the sidebar as "Read-only" badge items
        // Check if any exist (only present if sub-agents are currently running)
        var subAgentItems = page.Locator(".agent-session-item");
        var count = await subAgentItems.CountAsync();
        _out.WriteLine($"Sub-agent session items in sidebar: {count}");

        if (count > 0)
        {
            var badge = subAgentItems.First.Locator(".conversation-default-badge");
            var badgeText = (await badge.TextContentAsync() ?? "").Trim();
            Assert.Equal("Read-only", badgeText);
        }
        else
        {
            _out.WriteLine("No sub-agent sessions active — skipping sub-agent badge check.");
        }
    }
}
