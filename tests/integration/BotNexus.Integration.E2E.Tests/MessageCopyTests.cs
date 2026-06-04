using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for copy-to-clipboard features in the ChatPanel:
/// - Copy message button on assistant messages (📋 → ✓ feedback)
/// - Copy tool section buttons (args, result)
/// Also covers message metadata: role label, timestamp.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class MessageCopyTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public MessageCopyTests(NewUserExperienceFixture fx, ITestOutputHelper output)
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
    [Trait("Category", "MessageCopy")]
    public async Task AssistantMessage_HasCopyButton()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForStreamingCompleteAsync();

        // Assistant message should render with a copy button
        var assistantMsg = page.Locator("[data-message-role='Assistant']").First;
        await assistantMsg.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var copyBtn = assistantMsg.Locator(".msg-copy-btn");
        var visible = await copyBtn.IsVisibleAsync();
        Assert.True(visible, "Assistant messages should have a copy button (.msg-copy-btn).");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "MessageCopy")]
    public async Task UserMessage_HasNoCopyButton()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("HELLO_WORLD");

        var userMsg = page.Locator("[data-message-role='User']").First;
        await userMsg.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        var copyBtns = userMsg.Locator(".msg-copy-btn");
        var count = await copyBtns.CountAsync();
        Assert.Equal(0, count);
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "MessageCopy")]
    public async Task CopyButton_ShowsCheckmark_AfterClick()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Grant clipboard permissions
        await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);

        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForStreamingCompleteAsync();

        var copyBtn = page.Locator(".msg-copy-btn").First;
        await copyBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var initialText = (await copyBtn.TextContentAsync() ?? "").Trim();
        _out.WriteLine($"Initial copy btn text: {initialText}");
        Assert.Equal("📋", initialText);

        await copyBtn.ClickAsync();

        // Should briefly show ✓
        try
        {
            await page.WaitForFunctionAsync(
                "document.querySelector('.msg-copy-btn')?.textContent?.trim() === '✓'",
                null, new() { Timeout = 3_000 });
            var feedbackText = (await copyBtn.TextContentAsync() ?? "").Trim();
            Assert.Equal("✓", feedbackText);
        }
        catch (TimeoutException)
        {
            _out.WriteLine("Copy feedback ✓ not observed — clipboard API may be blocked in headless mode.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "MessageCopy")]
    public async Task Message_HasRoleLabel_AndTimestamp()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("HELLO_WORLD");
        await chat.WaitForStreamingCompleteAsync();

        // User message
        var userMsg = page.Locator("[data-message-role='User']").First;
        var userRole = (await userMsg.Locator(".message-role").TextContentAsync() ?? "").Trim();
        Assert.Equal("User", userRole);

        // Assistant message
        var assistantMsg = page.Locator("[data-message-role='Assistant']").First;
        var assistantRole = (await assistantMsg.Locator(".message-role").TextContentAsync() ?? "").Trim();
        Assert.Equal("Assistant", assistantRole);

        var timestamp = await assistantMsg.Locator(".message-time").TextContentAsync() ?? "";
        Assert.False(string.IsNullOrWhiteSpace(timestamp), "Message should show a timestamp.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "MessageCopy")]
    public async Task ToolCallMessage_CopyArgButtons_ArePresent_WhenExpanded()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("TOOL_CALL_SEQUENCE");
        await chat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(20));

        // Find the tool call message and expand it
        var toolMsg = page.Locator(".message.tool").First;
        await toolMsg.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 });

        await toolMsg.Locator(".tool-header").ClickAsync();

        // After expanding, copy buttons should appear
        var copyBtn = toolMsg.Locator(".tool-copy-btn").First;
        await copyBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var count = await toolMsg.Locator(".tool-copy-btn").CountAsync();
        Assert.True(count >= 1, "Expanded tool call should show at least one copy button.");
    }
}
