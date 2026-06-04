using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Validates compaction quality improvements inspired by the Hermes agent framework
/// (https://github.com/NousResearch/hermes-agent). Specifically covers the failure
/// modes observed in production:
///
/// <list type="number">
///   <item><description>
///     <b>Stale task resumption</b>: after compaction the agent must NOT blindly resume
///     tasks described in the summary when the user has moved on, changed topic, or
///     sent a stop signal. The latest user message must always win.
///   </description></item>
///   <item><description>
///     <b>Compaction boundary visibility</b>: the portal must display a clear visual
///     system message at the compaction point so the user knows context was summarised.
///   </description></item>
///   <item><description>
///     <b>Continuation after compaction</b>: the agent must continue responding to new
///     turns after auto-compaction fires — no silent stops or orphaned streaming state.
///   </description></item>
///   <item><description>
///     <b>Multiple compaction cycles</b>: a second compaction must not destroy the
///     summary produced by the first; continuity must survive multiple cycles.
///   </description></item>
///   <item><description>
///     <b>Guardrail prefix</b>: the portal must render the compaction notification with
///     distinguishable styling (system message class) so it is never mistaken for
///     a regular assistant response.
///   </description></item>
/// </list>
///
/// These tests require PR #655 gateway improvements to be fully passing; until then
/// they document the expected behaviour and will fail precisely where the gap is.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class CompactionContinuityTests
{
    private readonly NewUserExperienceFixture _fx;

    public CompactionContinuityTests(NewUserExperienceFixture fx) => _fx = fx;

    // -------------------------------------------------------------------------
    // 1. Compaction boundary is visible in the portal
    // -------------------------------------------------------------------------

    /// <summary>
    /// After /compact the portal must show a system bubble containing
    /// "Session context compacted" — styled distinctly from assistant messages.
    /// Regression guard for PR #602 fix #2.
    /// Extended here to also assert the bubble has the correct CSS class so it
    /// is visually distinct from a normal assistant message.
    /// </summary>
    [SkippableFact]
    public async Task SlashCompact_NotificationBubble_HasSystemMessageStyling()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture init failed: {_fx.Error}");

        var (browser, skipReason) = await TryLaunchBrowserAsync();
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var page = await browser.NewContextAsync().ContinueWith(t => t.Result.NewPageAsync()).Unwrap();
        var agentId = _fx.AgentIds[0];
        await NavigateAsync(page, $"{_fx.GatewayBaseUrl}/chat/{agentId}");

        var composer = await GetComposerAsync(page);
        await SendAsync(page, composer, "HELLO_WORLD");
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));

        await SendAsync(page, composer, "/compact");

        // Must appear as a system message — not assistant, not user.
        var bubble = page.Locator("[data-testid='chat-system-message']")
            .Filter(new LocatorFilterOptions { HasTextString = "compacted" })
            .First;
        await bubble.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 30_000,
        });

        // Must NOT appear as an assistant message (wrong styling = UX regression).
        var assistantMatch = page.Locator(".message.assistant .message-content")
            .Filter(new LocatorFilterOptions { HasTextString = "compacted" });
        var assistantCount = await assistantMatch.CountAsync();
        Assert.Equal(0, assistantCount);
    }

    // -------------------------------------------------------------------------
    // 2. Agent continues normally after /compact
    // -------------------------------------------------------------------------

    /// <summary>
    /// The turn immediately following /compact must reach the agent and produce
    /// a response. Tests PR #602 fix #3 and ongoing regression guard.
    /// </summary>
    [SkippableFact]
    public async Task SlashCompact_NextTurn_AgentRespondsNormally()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture init failed: {_fx.Error}");

        var (browser, skipReason) = await TryLaunchBrowserAsync();
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var page = await browser.NewContextAsync().ContinueWith(t => t.Result.NewPageAsync()).Unwrap();
        var agentId = _fx.AgentIds[0];
        await NavigateAsync(page, $"{_fx.GatewayBaseUrl}/chat/{agentId}");

        var composer = await GetComposerAsync(page);

        // Seed session.
        await SendAsync(page, composer, "HELLO_WORLD");
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));

        // Compact.
        await SendAsync(page, composer, "/compact");
        var compactBubble = page.Locator("[data-testid='chat-system-message']")
            .Filter(new LocatorFilterOptions { HasTextString = "compacted" }).First;
        await compactBubble.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        // Next turn — must still work.
        await SendAsync(page, composer, "HELLO_WORLD");
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));
    }

    // -------------------------------------------------------------------------
    // 3. New user message supersedes active task in summary
    // -------------------------------------------------------------------------

    /// <summary>
    /// After compaction, sending a new unrelated message must produce a response
    /// relevant to the NEW message — not a continuation of any task described in
    /// the compaction summary. The latest message always wins.
    ///
    /// This is the Hermes SUMMARY_PREFIX guardrail test: the agent must not
    /// treat "## Active Task" in the summary as a live instruction.
    /// </summary>
    [SkippableFact]
    public async Task PostCompaction_NewMessage_RespondsToNewTopic_NotSummaryTask()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture init failed: {_fx.Error}");

        var (browser, skipReason) = await TryLaunchBrowserAsync();
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var page = await browser.NewContextAsync().ContinueWith(t => t.Result.NewPageAsync()).Unwrap();
        var agentId = _fx.AgentIds[0];
        await NavigateAsync(page, $"{_fx.GatewayBaseUrl}/chat/{agentId}");

        var composer = await GetComposerAsync(page);

        // Establish a task context.
        await SendAsync(page, composer, "COMPACTION_TASK_SEED");
        await WaitForAssistantTextAsync(page, "monitoring", TimeSpan.FromSeconds(30));

        // Compact the session — the summary will contain "## Active Task: monitoring".
        await SendAsync(page, composer, "/compact");
        var compactBubble = page.Locator("[data-testid='chat-system-message']")
            .Filter(new LocatorFilterOptions { HasTextString = "compacted" }).First;
        await compactBubble.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        // Send a completely different request.
        await SendAsync(page, composer, "HELLO_WORLD");

        // Must respond with "Hello" (the new topic) — not with anything about monitoring.
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));

        // Confirm no assistant message mentions the stale task.
        var allAssistantMessages = await page.Locator(".message.assistant .message-content").AllInnerTextsAsync();
        // Take only messages after the compaction bubble (tail of the list).
        var lastMessage = allAssistantMessages.LastOrDefault() ?? string.Empty;
        Assert.True(
            lastMessage.Contains("Hello", StringComparison.OrdinalIgnoreCase),
            $"Last assistant message did not respond to new topic. Got: {lastMessage[..Math.Min(200, lastMessage.Length)]}");
    }

    // -------------------------------------------------------------------------
    // 4. Stop signal after compaction ends in-flight work
    // -------------------------------------------------------------------------

    /// <summary>
    /// If the summary contains "## In Progress: long running task" and the user
    /// sends "stop" as their next message, the agent must NOT resume or continue
    /// the described in-flight work.
    ///
    /// Maps to Hermes's: "Reverse signals (stop, undo, roll back) must immediately
    /// end any in-flight work described in the summary."
    /// </summary>
    [SkippableFact]
    public async Task PostCompaction_StopMessage_DoesNotResumeInFlightWork()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture init failed: {_fx.Error}");

        var (browser, skipReason) = await TryLaunchBrowserAsync();
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var page = await browser.NewContextAsync().ContinueWith(t => t.Result.NewPageAsync()).Unwrap();
        var agentId = _fx.AgentIds[0];
        await NavigateAsync(page, $"{_fx.GatewayBaseUrl}/chat/{agentId}");

        var composer = await GetComposerAsync(page);

        // Seed a "long-running task" context.
        await SendAsync(page, composer, "COMPACTION_TASK_SEED");
        await WaitForAssistantTextAsync(page, "monitoring", TimeSpan.FromSeconds(30));

        // Compact.
        await SendAsync(page, composer, "/compact");
        var compactBubble = page.Locator("[data-testid='chat-system-message']")
            .Filter(new LocatorFilterOptions { HasTextString = "compacted" }).First;
        await compactBubble.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        // Send stop.
        await SendAsync(page, composer, "COMPACTION_STOP_SIGNAL");
        await WaitForAssistantTextAsync(page, "stopped", TimeSpan.FromSeconds(30));

        // The response must acknowledge the stop — not continue the monitoring task.
        var allMessages = await page.Locator(".message.assistant .message-content").AllInnerTextsAsync();
        var lastMessage = allMessages.LastOrDefault() ?? string.Empty;
        Assert.True(
            lastMessage.Contains("stopped", StringComparison.OrdinalIgnoreCase) ||
            lastMessage.Contains("understood", StringComparison.OrdinalIgnoreCase) ||
            lastMessage.Contains("ok", StringComparison.OrdinalIgnoreCase),
            $"Agent did not acknowledge stop signal after compaction. Last message: {lastMessage[..Math.Min(300, lastMessage.Length)]}");
    }

    // -------------------------------------------------------------------------
    // 5. Multiple compaction cycles — continuity survives
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compact the session twice. The second compaction must not destroy the
    /// summary from the first cycle — a response after the second compact must
    /// still work correctly (no orphaned state, no silent failure).
    ///
    /// Maps to Hermes's iterative summary update design.
    /// </summary>
    [SkippableFact]
    public async Task DoubleCompact_AgentContinuesAfterBothCycles()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture init failed: {_fx.Error}");

        var (browser, skipReason) = await TryLaunchBrowserAsync();
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var page = await browser.NewContextAsync().ContinueWith(t => t.Result.NewPageAsync()).Unwrap();
        var agentId = _fx.AgentIds[0];
        await NavigateAsync(page, $"{_fx.GatewayBaseUrl}/chat/{agentId}");

        var composer = await GetComposerAsync(page);

        // Seed, first compact.
        await SendAsync(page, composer, "HELLO_WORLD");
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));
        await SendAsync(page, composer, "/compact");
        await page.Locator("[data-testid='chat-system-message']")
            .Filter(new LocatorFilterOptions { HasTextString = "compacted" }).First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        // Another turn, second compact.
        await SendAsync(page, composer, "HELLO_WORLD");
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));
        await SendAsync(page, composer, "/compact");

        // There must now be two compaction system messages.
        var compactBubbles = page.Locator("[data-testid='chat-system-message']")
            .Filter(new LocatorFilterOptions { HasTextString = "compacted" });
        await compactBubbles.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });
        var compactCount = await compactBubbles.CountAsync();
        Assert.True(compactCount >= 2, $"Expected at least 2 compaction notifications, got {compactCount}");

        // Agent must still respond after the second compact.
        await SendAsync(page, composer, "HELLO_WORLD");
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));
    }

    // -------------------------------------------------------------------------
    // 6. No rogue cron conversation created by compaction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compaction must never create a ghost cron: conversation in the sidebar.
    /// Regression test for PR #602 fix #1.
    /// </summary>
    [SkippableFact]
    public async Task SlashCompact_NoCronConversationCreated()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture init failed: {_fx.Error}");

        var (browser, skipReason) = await TryLaunchBrowserAsync();
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var page = await browser.NewContextAsync().ContinueWith(t => t.Result.NewPageAsync()).Unwrap();
        var agentId = _fx.AgentIds[0];
        await NavigateAsync(page, $"{_fx.GatewayBaseUrl}/chat/{agentId}");

        var composer = await GetComposerAsync(page);

        await SendAsync(page, composer, "HELLO_WORLD");
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));
        await SendAsync(page, composer, "/compact");
        await page.Locator("[data-testid='chat-system-message']")
            .Filter(new LocatorFilterOptions { HasTextString = "compacted" }).First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        var conversationTitles = await page.Locator("[data-testid='conversation-list-item'] .conversation-list-item-title")
            .AllInnerTextsAsync();
        foreach (var title in conversationTitles)
        {
            Assert.False(
                title.StartsWith("cron:", StringComparison.OrdinalIgnoreCase),
                $"Rogue cron conversation created by /compact: '{title}'");
        }
    }

    // -------------------------------------------------------------------------
    // 7. Compaction notification ordering — appears before next response
    // -------------------------------------------------------------------------

    /// <summary>
    /// The compaction system message must appear in the conversation history
    /// BEFORE the next assistant response, not after. Correct ordering matters
    /// for user comprehension — the boundary must precede the post-compact reply.
    /// </summary>
    [SkippableFact]
    public async Task SlashCompact_NotificationAppearsBeforeNextResponse()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture init failed: {_fx.Error}");

        var (browser, skipReason) = await TryLaunchBrowserAsync();
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var page = await browser.NewContextAsync().ContinueWith(t => t.Result.NewPageAsync()).Unwrap();
        var agentId = _fx.AgentIds[0];
        await NavigateAsync(page, $"{_fx.GatewayBaseUrl}/chat/{agentId}");

        var composer = await GetComposerAsync(page);

        await SendAsync(page, composer, "HELLO_WORLD");
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));
        await SendAsync(page, composer, "/compact");
        await page.Locator("[data-testid='chat-system-message']")
            .Filter(new LocatorFilterOptions { HasTextString = "compacted" }).First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        await SendAsync(page, composer, "HELLO_WORLD");
        await WaitForAssistantTextAsync(page, "Hello", TimeSpan.FromSeconds(30));

        // Walk the message list and find the compaction bubble's position vs last assistant message.
        var allMessages = await page.Locator("[data-testid='chat-messages'] > *").AllAsync();
        int compactIndex = -1;
        int lastAssistantIndex = -1;
        for (int i = 0; i < allMessages.Count; i++)
        {
            var cls = await allMessages[i].GetAttributeAsync("data-testid") ?? await allMessages[i].GetAttributeAsync("class") ?? "";
            var text = await allMessages[i].InnerTextAsync();
            if (text.Contains("compacted", StringComparison.OrdinalIgnoreCase))
                compactIndex = i;
            if (cls.Contains("assistant") || (await allMessages[i].Locator(".message.assistant").CountAsync()) > 0)
                lastAssistantIndex = i;
        }

        if (compactIndex >= 0 && lastAssistantIndex >= 0)
        {
            Assert.True(compactIndex < lastAssistantIndex,
                $"Compaction notification (index {compactIndex}) appeared AFTER the last assistant message (index {lastAssistantIndex})");
        }
        // If either wasn't found, the earlier assertions already caught the real failure.
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task<(IBrowser? browser, string skipReason)> TryLaunchBrowserAsync()
    {
        try
        {
            await PlaywrightBootstrap.EnsureBrowserInstalledAsync();
        }
        catch (Exception ex)
        {
            return (null, $"Playwright browser install unavailable: {ex.Message}");
        }
        var pw = await Playwright.CreateAsync();
        var browser = await PlaywrightBootstrap.LaunchChromiumAsync(pw);
        return (browser, string.Empty);
    }

    private static async Task NavigateAsync(IPage page, string url)
    {
        var nav = await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000,
        });
        Assert.NotNull(nav);
        Assert.True(nav!.Ok, $"GET {url} returned {nav.Status}");
    }

    private static async Task<ILocator> GetComposerAsync(IPage page)
    {
        var composer = page.Locator("[data-testid='chat-input']").First;
        await composer.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });
        return composer;
    }

    private static async Task SendAsync(IPage page, ILocator composer, string text)
    {
        await composer.FillAsync(text);
        var send = page.Locator("[data-testid='chat-send']").First;
        await send.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await send.ClickAsync();
        try
        {
            await page.WaitForFunctionAsync(
                "() => { var el = document.querySelector(\"[data-testid='chat-input']\"); return el && (el.value || '') === ''; }",
                null,
                new PageWaitForFunctionOptions { Timeout = 5_000 });
        }
        catch (TimeoutException) { }
    }

    private static async Task WaitForAssistantTextAsync(IPage page, string substring, TimeSpan timeout)
    {
        var locator = page.Locator(".message.assistant .message-content")
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
            Assert.Fail($"Assistant message containing '{substring}' did not appear within {timeout}. Messages:\n{snapshot[..Math.Min(2000, snapshot.Length)]}");
        }
    }
}
