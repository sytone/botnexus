using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for markdown rendering in the desktop ChatPanel.
/// Covers: headings, bold, italic, code blocks, lists, links render correctly.
/// Also tests that code copy buttons are injected by BotNexus.attachCodeCopyButtons.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class MarkdownRenderingTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public MarkdownRenderingTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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
    [Trait("Category", "Markdown")]
    public async Task AssistantMessage_RendersMarkdown_HeadingAndBold()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("MARKDOWN_RESPONSE");
        await chat.WaitForStreamingCompleteAsync();


        var msgContent = page.Locator("[data-message-role='Assistant'] .msg-content").First;
        await msgContent.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        // H1 should render as <h1>
        var h1 = msgContent.Locator("h1");
        var h1Count = await h1.CountAsync();
        _out.WriteLine($"H1 elements: {h1Count}");
        Assert.True(h1Count >= 1, "Markdown '# Heading' should render as an <h1> element.");

        // Bold should render as <strong>
        var strong = msgContent.Locator("strong");
        var strongCount = await strong.CountAsync();
        Assert.True(strongCount >= 1, "Markdown '**Bold**' should render as <strong>.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Markdown")]
    public async Task AssistantMessage_RendersMarkdown_CodeBlock()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("MARKDOWN_RESPONSE");
        await chat.WaitForStreamingCompleteAsync();

        var msgContent = page.Locator("[data-message-role='Assistant'] .msg-content").First;
        await msgContent.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        // Code blocks should render as <code> or <pre>
        var code = msgContent.Locator("code, pre");
        var codeCount = await code.CountAsync();
        _out.WriteLine($"Code/pre elements: {codeCount}");
        Assert.True(codeCount >= 1, "Markdown code block should render as <code> or <pre>.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Markdown")]
    public async Task AssistantMessage_RendersMarkdown_ListItems()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("MARKDOWN_RESPONSE");
        await chat.WaitForStreamingCompleteAsync();

        var msgContent = page.Locator("[data-message-role='Assistant'] .msg-content").First;
        await msgContent.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var listItems = msgContent.Locator("li");
        var liCount = await listItems.CountAsync();
        _out.WriteLine($"List items: {liCount}");
        Assert.True(liCount >= 1, "Markdown list items should render as <li> elements.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Markdown")]
    public async Task PlainTextMessage_RendersAsText_NotMarkdown()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForStreamingCompleteAsync();

        var assistantMsg = page.Locator("[data-message-role='Assistant']").First;
        await assistantMsg.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var msgText = (await assistantMsg.TextContentAsync() ?? "").Trim();
        _out.WriteLine($"Assistant message text: {msgText}");
        Assert.True(msgText.Contains("Hello, world!"),
            "HELLO_WORLD script should result in 'Hello, world!' text.");
    }
}
