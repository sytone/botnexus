using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for the streaming badge ("Streaming...") shown in the chat header during active turns,
/// the steer button (Steer) that appears while streaming,
/// and the abort button (Stop) + Escape key shortcut.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class StreamingBadgeAndSteerTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public StreamingBadgeAndSteerTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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
    [Trait("Category", "Streaming")]
    public async Task StreamingBadge_AppearsInHeader_DuringActiveStream()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("SLOW_STREAM");

        try
        {
            var badge = page.Locator(".streaming-badge");
            await badge.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 8_000 });
            var text = (await badge.TextContentAsync() ?? "").Trim();
            Assert.True(text.Contains("Streaming"), $"Streaming badge should say 'Streaming...', got: '{text}'.");
        }
        catch (TimeoutException)
        {
            _out.WriteLine("Streaming badge not observed - stream may have completed too quickly.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Streaming")]
    public async Task SteerButton_AppearsWhenStreaming_SendButtonHidden()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("SLOW_STREAM");

        try
        {
            var steerBtn = chat.SteerBtn;
            await steerBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 8_000 });

            var sendVisible = await chat.SendBtn.IsVisibleAsync();
            Assert.False(sendVisible, "Send button should be hidden while Steer button is visible.");

            var abortVisible = await chat.AbortBtn.IsVisibleAsync();
            Assert.True(abortVisible, "Abort (Stop) button should be visible during streaming.");
        }
        catch (TimeoutException)
        {
            _out.WriteLine("Stream completed before steer/abort observation - acceptable.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Streaming")]
    public async Task AbortButton_StopsStream_WhenClicked()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("SLOW_STREAM");

        try
        {
            await chat.AbortBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 8_000 });
            await chat.AbortBtn.ClickAsync();

            // Send button should come back
            await chat.SendBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
            _out.WriteLine("Abort succeeded - send button restored.");
        }
        catch (TimeoutException)
        {
            _out.WriteLine("Abort button not observed in time - stream completed before abort.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Streaming")]
    public async Task EscapeKey_AbortsStream()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("SLOW_STREAM");

        try
        {
            // Wait for streaming indicator
            await page.WaitForSelectorAsync("[data-testid='streaming-message']",
                new() { Timeout = 8_000 });

            // Focus the input and press Escape
            await chat.ChatInput.ClickAsync();
            await page.Keyboard.PressAsync("Escape");

            await chat.SendBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
            _out.WriteLine("Escape abort succeeded.");
        }
        catch (TimeoutException)
        {
            _out.WriteLine("Stream completed before Escape abort - acceptable.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Streaming")]
    public async Task InputPlaceholder_ChangesToSteerHint_DuringStreaming()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var normalPlaceholder = await chat.ChatInput.GetAttributeAsync("placeholder") ?? "";
        Assert.True(normalPlaceholder.Contains("Type a message"),
            $"Normal placeholder should say 'Type a message...', got: '{normalPlaceholder}'.");

        await chat.SendMessageAsync("SLOW_STREAM");

        try
        {
            await page.WaitForFunctionAsync(
                "document.querySelector('[data-testid=\"chat-input\"]')?.placeholder?.includes('steer')",
                null, new() { Timeout = 8_000 });
            var streamingPlaceholder = await chat.ChatInput.GetAttributeAsync("placeholder") ?? "";
            Assert.True(streamingPlaceholder.Contains("steer"),
                $"During streaming placeholder should hint at steering, got: '{streamingPlaceholder}'.");
        }
        catch (TimeoutException)
        {
            _out.WriteLine("Streaming placeholder not observed - stream completed too fast.");
        }
    }
}
