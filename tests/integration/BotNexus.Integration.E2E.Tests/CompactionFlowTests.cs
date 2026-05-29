using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Verifies the user-triggered <c>/compact</c> slash command path matches the
/// behaviour fixed in PR #602 + the unification done on top of it:
///
/// <list type="number">
///   <item><description>A canonical <c>[Session context compacted: …]</c> system bubble appears in the active conversation.</description></item>
///   <item><description>No spurious <c>cron:</c> (virtual session) conversation is created in the conversation list.</description></item>
///   <item><description>The agent continues responding to subsequent user messages after the compact.</description></item>
/// </list>
///
/// The flow exercises <c>GatewayHub.CompactSession</c> → <c>ISessionCompactionCoordinator</c>
/// — the same coordinator the auto-compact (token threshold) path now uses,
/// so a single E2E proves both call sites behave identically end-to-end.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class CompactionFlowTests
{
    private readonly NewUserExperienceFixture _fx;

    public CompactionFlowTests(NewUserExperienceFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task SlashCompact_NotifiesUser_NoCronConversation_ContinuesNormally()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");

        try
        {
            await PlaywrightBootstrap.EnsureBrowserInstalledAsync();
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Playwright browser install unavailable: {ex.Message}");
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var agentId = _fx.AgentIds[0];

        var nav = await page.GotoAsync($"{_fx.GatewayBaseUrl}/chat/{agentId}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000,
        });
        Xunit.Assert.NotNull(nav);
        Xunit.Assert.True(nav!.Ok, $"GET /chat/{agentId} returned {nav.Status}");

        var composer = page.Locator("[data-testid='chat-input']").First;
        await composer.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });

        // Seed the session with one user turn so there is something to compact.
        await SendAsync(page, composer, "HELLO_WORLD");
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));

        // Trigger the user-initiated compaction path.
        await SendAsync(page, composer, "/compact");

        // PR #602 fix #2: the canonical notification must surface to the user.
        var systemMessage = page.Locator("[data-testid='chat-system-message']")
            .Filter(new LocatorFilterOptions { HasTextString = "Session context compacted" })
            .First;
        try
        {
            await systemMessage.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = 30_000,
            });
        }
        catch (TimeoutException)
        {
            var snapshot = await page.Locator("[data-testid='chat-messages']").First.InnerHTMLAsync();
            Xunit.Assert.Fail($"Compaction notification did not appear within 30s. Messages container:\n{Truncate(snapshot)}");
        }

        // PR #602 fix #1: no virtual 'cron:'-kind conversation must be created.
        var conversationTitles = await page.Locator("[data-testid='conversation-list-item'] .conversation-list-item-title")
            .AllInnerTextsAsync();
        foreach (var title in conversationTitles)
        {
            Xunit.Assert.False(
                title.StartsWith("cron:", StringComparison.OrdinalIgnoreCase),
                $"Unexpected cron-prefixed conversation created by /compact: '{title}'");
        }

        // PR #602 fix #3: the next user turn must reach the agent and elicit a response.
        await SendAsync(page, composer, "HELLO_WORLD");
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));
    }

    private static async Task SendAsync(IPage page, ILocator composer, string text)
    {
        await composer.FillAsync(text);
        var send = page.Locator("[data-testid='chat-send']").First;
        await send.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
        await send.ClickAsync();
        // Wait for the composer to clear, signalling Blazor processed the send.
        try
        {
            await page.WaitForFunctionAsync(
                "() => { var el = document.querySelector(\"[data-testid='chat-input']\"); return el && (el.value || '') === ''; }",
                null,
                new PageWaitForFunctionOptions { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
            // Slash commands clear synchronously too; if this races, the
            // assertions below will surface the real failure mode.
        }
    }

    private static async Task WaitForAssistantTextAsync(IPage page, string substring, TimeSpan timeout)
    {
        // ChatPanel.razor renders assistant messages with two different content classes:
        //   - `msg-content` for markdown-rendered turns (the normal completed path)
        //   - `message-content` for plain-text and active streaming buffer
        // Match either so the test does not silently time out on rendered markdown.
        var locator = page.Locator(".message.assistant .msg-content, .message.assistant .message-content")
            .Filter(new LocatorFilterOptions { HasTextString = substring })
            .First;
        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = (float)timeout.TotalMilliseconds,
            });
        }
        catch (TimeoutException)
        {
            var snapshot = await page.Locator("[data-testid='chat-messages']").First.InnerHTMLAsync();
            Xunit.Assert.Fail($"Assistant message containing '{substring}' did not appear within {timeout}. Messages:\n{Truncate(snapshot)}");
        }
    }

    private static string Truncate(string text) =>
        text.Length <= 2000 ? text : text[..2000] + "…";
}
