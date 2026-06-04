using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for thinking block display in the ChatPanel.
/// Covers: thinking block renders as <details> with summary,
/// thinking toggle hides/shows blocks, live thinking indicator during stream,
/// thinking block contains content after completion.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ThinkingBlockTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public ThinkingBlockTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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
    [Trait("Category", "Thinking")]
    public async Task ThinkingBlock_RendersAfterThinkingResponse()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("THINKING_BLOCK");
        await chat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(20));

        var thinkingBlock = page.Locator(".thinking-block").First;
        await thinkingBlock.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 });

        // Should contain a <details> with a summary
        var details = thinkingBlock.Locator("details");
        var detailsCount = await details.CountAsync();
        Assert.True(detailsCount >= 1, "Thinking block should use a <details> element.");

        var summary = details.Locator("summary");
        var summaryText = (await summary.TextContentAsync() ?? "").Trim();
        _out.WriteLine($"Thinking summary: {summaryText}");
        Assert.True(summaryText.Contains("Thinking"), "Thinking summary should say 'Thinking…'.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Thinking")]
    public async Task ThinkingToggle_HidesThinkingBlocks_WhenTurnedOff()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("THINKING_BLOCK");
        await chat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(20));

        var thinkingBlock = page.Locator(".thinking-block").First;
        await thinkingBlock.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 });

        // Initial state: thinking should be visible (ShowThinking defaults to true)
        var style = await thinkingBlock.GetAttributeAsync("style") ?? "";
        _out.WriteLine($"Initial thinking block style: {style}");

        // Click thinking toggle
        var thinkingToggle = page.Locator(".toggle-btn[title='Toggle thinking visibility']").First;
        await thinkingToggle.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await thinkingToggle.ClickAsync();

        // Thinking block should now be hidden via display:none
        await page.WaitForTimeoutAsync(200);
        style = await thinkingBlock.GetAttributeAsync("style") ?? "";
        _out.WriteLine($"After toggle thinking block style: {style}");
        Assert.True(style.Contains("display:none") || style.Contains("display: none"),
            "Thinking block should be hidden when thinking toggle is off.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Thinking")]
    public async Task ThinkingToggle_RestoresVisibility_WhenTurnedBackOn()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("THINKING_BLOCK");
        await chat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(20));

        var thinkingBlock = page.Locator(".thinking-block").First;
        await thinkingBlock.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 });

        var toggle = page.Locator(".toggle-btn[title='Toggle thinking visibility']").First;
        await toggle.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        // Off
        await toggle.ClickAsync();
        await page.WaitForTimeoutAsync(200);

        // On again
        await toggle.ClickAsync();
        await page.WaitForTimeoutAsync(200);

        var style = await thinkingBlock.GetAttributeAsync("style") ?? "";
        Assert.False(style.Contains("display:none") || style.Contains("display: none"),
            "Thinking block should be visible after toggle turned back on.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Thinking")]
    public async Task ThinkingBlock_ContainsThinkingContent()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.SendMessageAsync("THINKING_BLOCK");
        await chat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(20));

        var thinkingContent = page.Locator(".thinking-content").First;
        await thinkingContent.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 });

        // Open the details element to make content visible
        await page.Locator(".thinking-block details").First.EvaluateAsync("el => el.open = true");

        var content = (await thinkingContent.TextContentAsync() ?? "").Trim();
        _out.WriteLine($"Thinking content: {content}");
        Assert.False(string.IsNullOrWhiteSpace(content), "Thinking block should contain thinking text.");
        Assert.True(content.Contains("reason") || content.Length > 5,
            "Thinking content should have meaningful text.");
    }
}
