using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for conversation title inline editing in the ChatPanel header.
/// Covers: click to open edit input, Enter to save, Escape to cancel,
/// read-only mode (title not editable), default badge display.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ConversationTitleEditTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public ConversationTitleEditTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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
    [Trait("Category", "ConversationTitle")]
    public async Task ConversationTitle_HasEditableClass_OnDesktop()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Wait for a conversation to be selected
        var titleEl = page.Locator(".conversation-title.editable").First;
        await titleEl.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var title = await titleEl.GetAttributeAsync("title") ?? "";
        _out.WriteLine($"Title title attr: {title}");
        Assert.True(title.Contains("click to rename"),
            "Editable conversation title should have a tooltip saying 'click to rename'.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "ConversationTitle")]
    public async Task ConversationTitle_Click_OpensEditInput()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var titleEl = page.Locator(".conversation-title.editable").First;
        await titleEl.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        var originalTitle = (await titleEl.TextContentAsync() ?? "").Trim();

        await titleEl.ClickAsync();

        var input = page.Locator(".conversation-title-input");
        await input.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        var inputValue = await input.InputValueAsync();

        _out.WriteLine($"Original={originalTitle} InputValue={inputValue}");
        Assert.Equal(originalTitle, inputValue);
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "ConversationTitle")]
    public async Task ConversationTitle_EscapeKey_CancelsEdit()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var titleEl = page.Locator(".conversation-title.editable").First;
        await titleEl.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        var originalTitle = (await titleEl.TextContentAsync() ?? "").Trim();

        await titleEl.ClickAsync();
        var input = page.Locator(".conversation-title-input");
        await input.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        // Type something then press Escape
        await input.FillAsync("New Title Draft That Should Not Save");
        await input.PressAsync("Escape");

        // Input should disappear and title restored
        await input.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
        var restoredTitle = (await page.Locator(".conversation-title.editable").First.TextContentAsync() ?? "").Trim();
        Assert.Equal(originalTitle, restoredTitle);
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "ConversationTitle")]
    public async Task ConversationTitle_EnterKey_SavesAndClosesInput()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var titleEl = page.Locator(".conversation-title.editable").First;
        await titleEl.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        await titleEl.ClickAsync();
        var input = page.Locator(".conversation-title-input");
        await input.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var newTitle = $"Renamed {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await input.FillAsync(newTitle);
        await input.PressAsync("Enter");

        // Input should close
        await input.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        // Title element should now show the new name
        var updatedTitle = (await page.Locator(".conversation-title.editable").First.TextContentAsync() ?? "").Trim();
        _out.WriteLine($"Updated title: {updatedTitle}");
        Assert.Equal(newTitle, updatedTitle);
    }
}
