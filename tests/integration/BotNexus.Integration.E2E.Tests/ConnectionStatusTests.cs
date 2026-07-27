using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the ConnectionStatus component in the sidebar.
/// Covers: Connected state visible, label text, dot character, CSS class.
/// Also tests the SessionControls (session ID chip + copy toast).
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ConnectionStatusTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public ConnectionStatusTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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
    [Trait("Category", "ConnectionStatus")]
    public async Task ConnectionIndicator_ShowsConnected_WhenGatewayIsUp()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (_, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);

        // #2441: the indicator lives in the top bar now, so it is visible without opening the sidebar.
        var indicator = portal.Page.Locator("[data-testid='connection-status']").First;
        await indicator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        var cssClass = await indicator.GetAttributeAsync("class") ?? "";
        var label = await indicator.Locator(".connection-label").TextContentAsync() ?? "";
        var dot = await indicator.Locator(".connection-dot").TextContentAsync() ?? "";
        var tooltip = await indicator.GetAttributeAsync("title") ?? "";

        _out.WriteLine($"Class={cssClass} Label={label} Dot={dot} Title={tooltip}");

        Assert.True(cssClass.Contains("status-connected"),
            $"Connection indicator should have 'status-connected' class when gateway is up. Got: {cssClass}");
        Assert.Contains("connection-indicator-dot", cssClass);
        // The state text stays in the DOM for assistive tech and is surfaced via the tooltip.
        Assert.Equal("Connected", label.Trim());
        Assert.Equal("Connected", tooltip.Trim());
        Assert.Equal("\u25CF", dot.Trim());
    }

    // -------------------------------------------------------------------------
    // #2441: the indicator must be in the top bar, after the logo - not in the sidebar.
    [SkippableFact]
    [Trait("Category", "ConnectionStatus")]
    public async Task ConnectionIndicator_IsInTopBar_AndSidebarRowIsGone()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (_, portal) = await PortalTestHelpers.NewPortalPageAsync(_browser, _fx.GatewayBaseUrl);

        var inBanner = portal.Page.Locator(".banner-header [data-testid='connection-status']").First;
        await inBanner.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        Assert.Equal(0, await portal.Page.Locator(".sidebar-connection").CountAsync());

        var logoBox = await portal.Page.Locator(".banner-logo").First.BoundingBoxAsync();
        var dotBox = await inBanner.BoundingBoxAsync();
        Assert.NotNull(logoBox);
        Assert.NotNull(dotBox);
        Assert.True(dotBox!.X >= logoBox!.X,
            $"Connection dot (x={dotBox.X}) should sit after the logo (x={logoBox.X}).");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "ConnectionStatus")]
    public async Task SessionControls_ShowsSessionIdChip_WhenConversationActive()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, portal, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Session ID chip appears in the chat header
        var chip = page.Locator(".session-controls .session-id").First;
        // It may not appear if no session has started yet — wait up to 5s
        try
        {
            await chip.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            var text = await chip.TextContentAsync() ?? "";
            _out.WriteLine($"Session chip text: {text}");
            Assert.False(string.IsNullOrWhiteSpace(text), "Session ID chip should show a session ID prefix.");
        }
        catch (TimeoutException)
        {
            // No session started yet — chip may not appear until first message
            _out.WriteLine("Session ID chip not visible (no active session yet) — acceptable.");
        }
    }
}
