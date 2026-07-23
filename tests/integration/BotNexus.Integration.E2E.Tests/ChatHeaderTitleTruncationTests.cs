using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Regression coverage for #2141: a very long conversation title must stay
/// clipped inside the header title region and must not overlap the fixed-width
/// header action controls (thinking/tools/pin/archive/config/new-session).
/// The title region has to be shrinkable so the ellipsis engages and the action
/// group remains fully visible and clickable.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ChatHeaderTitleTruncationTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public ChatHeaderTitleTruncationTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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

    private const string LongTitle =
        "This is an extremely long conversation title that would otherwise overflow the chat header and paint on top of the thinking tools pin archive config and new session action controls at any breakpoint";

    private static async Task RenameActiveConversationAsync(IPage page, string newTitle)
    {
        var titleEl = page.Locator(".conversation-title.editable").First;
        await titleEl.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await titleEl.ClickAsync();

        var input = page.Locator(".conversation-title-input");
        await input.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await input.FillAsync(newTitle);
        await input.PressAsync("Enter");
        await input.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
    }

    private async Task AssertTitleDoesNotOverlapActionsAsync(IPage page)
    {
        var titleBox = await page.Locator(".conversation-title").First.BoundingBoxAsync();
        var actionsBox = await page.Locator(".chat-header-actions").First.BoundingBoxAsync();
        Assert.NotNull(titleBox);
        Assert.NotNull(actionsBox);

        // The title's right edge must not cross the left edge of the action group.
        // A 1px tolerance covers sub-pixel rounding.
        var titleRight = titleBox!.X + titleBox.Width;
        _out.WriteLine($"titleRight={titleRight} actionsLeft={actionsBox!.X} titleWidth={titleBox.Width}");
        Assert.True(titleRight <= actionsBox.X + 1,
            $"Long title (right={titleRight}) overlaps header actions (left={actionsBox.X}); title region is not shrinking.");

        // The action group must be visible and non-empty.
        Assert.True(actionsBox.Width > 0, "Header action group collapsed to zero width.");
    }

    [SkippableFact]
    [Trait("Category", "ConversationTitle")]
    public async Task LongTitle_DoesNotOverlapHeaderActions_OnDesktop()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);
        await page.SetViewportSizeAsync(1280, 800);

        await RenameActiveConversationAsync(page, LongTitle);
        await AssertTitleDoesNotOverlapActionsAsync(page);

        // Full title preserved for hover/accessibility.
        var titleAttr = await page.Locator(".conversation-title").First.GetAttributeAsync("title") ?? "";
        Assert.False(string.IsNullOrEmpty(titleAttr));
    }

    [SkippableFact]
    [Trait("Category", "ConversationTitle")]
    public async Task LongTitle_DoesNotOverlapHeaderActions_AtNarrowBreakpoint()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Rename at desktop width first (edit input is easiest there), then
        // shrink to a constrained width where the actions group is still shown.
        await page.SetViewportSizeAsync(1280, 800);
        await RenameActiveConversationAsync(page, LongTitle);

        await page.SetViewportSizeAsync(680, 800);
        // Give layout a beat to reflow.
        await page.WaitForTimeoutAsync(200);

        // At this width the primary action group is still rendered (the mobile
        // overflow collapse only kicks in below 480px), so the title must yield.
        var actions = page.Locator(".chat-header-actions").First;
        if (await actions.IsVisibleAsync())
        {
            await AssertTitleDoesNotOverlapActionsAsync(page);
        }
    }
}
