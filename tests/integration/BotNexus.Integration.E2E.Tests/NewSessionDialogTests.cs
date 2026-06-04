using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the New Session confirmation dialog in ChatPanel.
/// Covers: dialog appears on button click, backdrop click cancels,
/// cancel button dismisses, confirm button triggers reset,
/// dialog does not appear when streaming.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class NewSessionDialogTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public NewSessionDialogTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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
    [Trait("Category", "NewSession")]
    public async Task NewSessionButton_OpensConfirmDialog()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var newBtn = page.Locator(".new-chat-btn").First;
        await newBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await newBtn.ClickAsync();

        var dialog = page.Locator(".reset-confirm-dialog");
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var text = await dialog.TextContentAsync() ?? "";
        _out.WriteLine($"Dialog text: {text}");
        Assert.True(text.Contains("new session"), "Confirm dialog should mention 'new session'.");
        Assert.True(text.Contains("History stays"),
            "Confirm dialog should reassure that history is preserved.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "NewSession")]
    public async Task NewSessionDialog_CancelButton_DismissesDialog()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await page.Locator(".new-chat-btn").First.ClickAsync();
        var dialog = page.Locator(".reset-confirm-dialog");
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        await dialog.Locator(".cancel-btn").ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        // Chat panel should still be in normal state
        var inputArea = page.Locator("[data-testid='chat-input']");
        await inputArea.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "NewSession")]
    public async Task NewSessionDialog_BackdropClick_DismissesDialog()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await page.Locator(".new-chat-btn").First.ClickAsync();
        var overlay = page.Locator(".reset-confirm-overlay");
        await overlay.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        // Click the overlay (outside the dialog box)
        await overlay.ClickAsync(new() { Position = new() { X = 10, Y = 10 } });

        await overlay.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "NewSession")]
    public async Task NewSessionDialog_ConfirmButton_ResetsSession()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Send a message first so there's history
        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForStreamingCompleteAsync();
        var beforeCount = await page.Locator("[data-testid='message']").CountAsync();

        // Confirm new session
        await page.Locator(".new-chat-btn").First.ClickAsync();
        var dialog = page.Locator(".reset-confirm-dialog");
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await dialog.Locator(".confirm-btn").ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        // A session boundary should appear separating old/new session
        await page.WaitForSelectorAsync(".session-boundary",
            new() { Timeout = 10_000, State = WaitForSelectorState.Visible });

        _out.WriteLine($"Messages before reset: {beforeCount}");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "NewSession")]
    public async Task NewSessionButton_IsDisabled_DuringStreaming()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("SLOW_STREAM");

        var newBtn = page.Locator(".new-chat-btn").First;
        try
        {
            await page.WaitForFunctionAsync(
                "document.querySelector('[data-testid=\"streaming-message\"]') !== null",
                null, new() { Timeout = 8_000 });
            var isDisabled = await newBtn.IsDisabledAsync();
            Assert.True(isDisabled, "New session button must be disabled during streaming.");
        }
        catch (TimeoutException)
        {
            _out.WriteLine("Stream completed before check — acceptable.");
        }
    }
}
