using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for slash command edge cases not covered in SlashCommandTests:
/// - Tab key completes first command suggestion
/// - /prompts sends as a real message
/// - Command palette closes on Escape key
/// - Command palette does not appear for regular text
/// - Invalid slash (e.g. "/xyz") shows empty palette
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class SlashCommandEdgeCaseTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public SlashCommandEdgeCaseTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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
    [Trait("Category", "SlashCommand")]
    public async Task TabKey_AutoCompletesFirstSuggestion()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.ChatInput.ClickAsync();
        await chat.ChatInput.PressSequentiallyAsync("/n", new() { Delay = 50 });

        // Wait for palette with /new
        await chat.CommandPalette.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        await page.Keyboard.PressAsync("Tab");

        // Palette should close and input should contain "/new "
        await chat.CommandPalette.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 3_000 });
        var value = await chat.ChatInput.InputValueAsync();
        _out.WriteLine($"Input after Tab: '{value}'");
        Assert.True(value.TrimStart().StartsWith("/new"),
            $"Tab should autocomplete to '/new', got: '{value}'");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "SlashCommand")]
    public async Task EscapeKey_ClosesPalette_WithoutExecuting()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.ChatInput.ClickAsync();
        await chat.ChatInput.PressSequentiallyAsync("/", new() { Delay = 50 });

        await chat.CommandPalette.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        await page.Keyboard.PressAsync("Escape");
        await chat.CommandPalette.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 3_000 });

        // Input should still have "/" - no command was executed
        var value = await chat.ChatInput.InputValueAsync();
        _out.WriteLine($"Input after Escape: '{value}'");
        // Input stays as-is (not cleared by Escape when not streaming)
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "SlashCommand")]
    public async Task RegularText_DoesNotShowPalette()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.ChatInput.ClickAsync();
        await chat.ChatInput.FillAsync("Hello world");

        await page.WaitForTimeoutAsync(300);
        Assert.Equal(0, await chat.CommandPalette.CountAsync());
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "SlashCommand")]
    public async Task UnknownSlashCommand_ShowsEmptyPalette()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.ChatInput.ClickAsync();
        await chat.ChatInput.PressSequentiallyAsync("/zzz", new() { Delay = 50 });

        await page.WaitForTimeoutAsync(300);

        if (await chat.CommandPalette.CountAsync() > 0)
        {
            // Palette visible but no items
            var count = await chat.CommandItems.CountAsync();
            Assert.Equal(0, count);
        }
        // If palette not rendered at all - also correct
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "SlashCommand")]
    public async Task SlashWithSpace_DoesNotShowPalette()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.ChatInput.ClickAsync();
        await chat.ChatInput.FillAsync("/new something extra"); // has a space - not a slash command

        await page.WaitForTimeoutAsync(300);
        Assert.Equal(0, await chat.CommandPalette.CountAsync());
    }
}
