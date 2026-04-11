using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class CommandPaletteE2ETests
{
    private const string AgentA = "agent-a";
    private readonly PlaywrightFixture _fixture;

    public CommandPaletteE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task SlashKey_OpensPalette()
    {
        var host = await OpenChatAsync();
        await host.Page.FillAsync("#chat-input", "/");
        await Assertions.Expect(host.Page.Locator("#command-palette")).ToBeVisibleAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task CtrlK_OpensPalette()
    {
        var host = await OpenChatAsync();
        await host.Page.EvaluateAsync(
            @"() => {
                document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, bubbles: true }));
            }");
        await Assertions.Expect(host.Page.Locator("#command-palette")).ToBeVisibleAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ArrowKeys_NavigatePalette()
    {
        var host = await OpenChatAsync();
        await host.Page.FillAsync("#chat-input", "/");
        await host.Page.PressAsync("#chat-input", "ArrowDown");
        await Assertions.Expect(host.Page.Locator("#command-palette .command-item.active .command-name")).ToContainTextAsync("/new");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task Enter_AcceptsPaletteSelection()
    {
        var host = await OpenChatAsync();
        await host.Page.FillAsync("#chat-input", "/");
        await host.Page.PressAsync("#chat-input", "ArrowDown");
        await host.Page.PressAsync("#chat-input", "Enter");
        await Assertions.Expect(host.Page.Locator("#chat-messages .message.system-msg")).ToContainTextAsync("New session started");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task Escape_DismissesPalette()
    {
        var host = await OpenChatAsync();
        await host.Page.FillAsync("#chat-input", "/");
        await Assertions.Expect(host.Page.Locator("#command-palette")).ToBeVisibleAsync();
        await host.Page.PressAsync("#chat-input", "Escape");
        await Assertions.Expect(host.Page.Locator("#command-palette")).ToBeHiddenAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task HelpCommand_ListsCommands()
    {
        var host = await OpenChatAsync();
        await host.Page.FillAsync("#chat-input", "/help");
        await host.Page.PressAsync("#chat-input", "Enter");
        await Assertions.Expect(host.Page.Locator("#chat-messages .command-result .command-result-title")).ToContainTextAsync("Available Commands");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ResetCommand_ResetsSession()
    {
        var host = await OpenChatAsync();
        await host.SendMessageAsync("before-reset");
        await host.WaitForStreamingCompleteAsync();

        await host.Page.FillAsync("#chat-input", "/reset");
        await host.Page.PressAsync("#chat-input", "Enter");

        await Assertions.Expect(host.Page.Locator("#chat-messages")).ToContainTextAsync("reset");
        await Assertions.Expect(host.Page.Locator("#session-id-display")).ToBeHiddenAsync();
    }

    private async Task<WebUiE2ETestHost> OpenChatAsync()
    {
        var host = await _fixture.CreatePageAsync();
        await host.OpenAgentTimelineAsync(AgentA);
        return host;
    }
}





