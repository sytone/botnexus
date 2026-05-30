using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Playwright-driven portal journey tests verifying real end-to-end user flows.
///
/// These tests use stable data-testid selectors added in PR #601:
///   - [data-testid="agent-card"]      — agent cards on AgentDashboard
///   - [data-testid="chat-composer"]   — the message textarea in ChatPanel
///   - [data-testid="chat-send"]       — the Send button in ChatPanel
///   - [data-testid="messages"]        — the messages scroll container
///   - [data-testid="message"]         — each completed message bubble
///   - [data-testid="streaming-message"] — the live in-progress bubble
///   - [data-testid="conversation-new"] — the "new conversation" button
///
/// All locators are scoped to #{agentId}-conversation-panel because the
/// multi-pane portal renders every configured agent concurrently — a global
/// data-testid match is ambiguous (4 agents, 4 composers).
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class PortalUserJourneyTests
{
    private readonly NewUserExperienceFixture _fx;

    public PortalUserJourneyTests(NewUserExperienceFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task PortalLoads_RendersAgentsFromConfig()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");
        try { await PlaywrightBootstrap.EnsureBrowserInstalledAsync(); }
        catch (Exception ex) { Skip.If(true, $"Playwright browser install unavailable: {ex.Message}"); }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync(_fx.GatewayBaseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000,
        });
        Xunit.Assert.NotNull(response);
        Xunit.Assert.True(response!.Ok, $"GET / returned {response.Status}");

        foreach (var id in _fx.AgentIds)
        {
            try
            {
                await page.GetByText(id, new PageGetByTextOptions { Exact = false })
                    .First
                    .WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Attached,
                        Timeout = 30_000,
                    });
            }
            catch (TimeoutException)
            {
                var snapshot = await page.ContentAsync();
                Xunit.Assert.Fail($"Portal did not render agent id '{id}' within 30s. HTML head:\n{snapshot[..Math.Min(2000, snapshot.Length)]}");
            }
        }
    }

    // ─── Followup flows for issue #598 ─────────────────────────────────────
    // These use stable data-testid selectors added to AgentDashboard, ChatPanel
    // and MainLayout so the assertions don't churn with cosmetic UI changes.

    [SkippableFact]
    public async Task NewConversation_PerAgent_SendHelloWorld()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");
        try { await PlaywrightBootstrap.EnsureBrowserInstalledAsync(); }
        catch (Exception ex) { Skip.If(true, $"Playwright browser install unavailable: {ex.Message}"); }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);

        foreach (var agentId in _fx.AgentIds)
        {
            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();

            await SendAndAwaitAsync(page, agentId, "HELLO_WORLD", "Hello, world!", timeoutMs: 60_000);

            await context.CloseAsync();
        }
    }
    [SkippableFact]
    public async Task ParallelMultiDelta_AcrossAgents_AllCompleteIndependently()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");
        try { await PlaywrightBootstrap.EnsureBrowserInstalledAsync(); }
        catch (Exception ex) { Skip.If(true, $"Playwright browser install unavailable: {ex.Message}"); }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);

        var tasks = _fx.AgentIds.Select(async agentId =>
        {
            var context = await browser.NewContextAsync();
            try
            {
                var page = await context.NewPageAsync();
                await SendAndAwaitAsync(page, agentId, "MULTI_DELTA", "[MULTI_DELTA_COMPLETE]", timeoutMs: 90_000);
            }
            finally { await context.CloseAsync(); }
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Seeds an existing conversation on the first agent, then concurrently:
    ///   (a) sends MULTI_DELTA against the existing conversation on agent[0]
    ///   (b) opens a fresh context on agent[1] and sends HELLO_WORLD
    /// Verifies both complete correctly with no cross-agent contamination.
    /// </summary>
    [SkippableFact]
    public async Task MixedExistingAndNewConversations_ConcurrentMessages()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");
        try { await PlaywrightBootstrap.EnsureBrowserInstalledAsync(); }
        catch (Exception ex) { Skip.If(true, $"Playwright browser install unavailable: {ex.Message}"); }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);

        // Seed an existing conversation on the first agent.
        var seedContext = await browser.NewContextAsync();
        var seedPage = await seedContext.NewPageAsync();
        await SendAndAwaitAsync(seedPage, _fx.AgentIds[0], "HELLO_WORLD", "Hello, world!", timeoutMs: 60_000);
        await seedContext.CloseAsync();

        // In parallel: reopen on agent[0] (existing conv auto-selected) + fresh on agent[1].
        var existing = Task.Run(async () =>
        {
            var ctx = await browser.NewContextAsync();
            try
            {
                var page = await ctx.NewPageAsync();
                await SendAndAwaitAsync(page, _fx.AgentIds[0], "MULTI_DELTA", "[MULTI_DELTA_COMPLETE]", timeoutMs: 90_000);
            }
            finally { await ctx.CloseAsync(); }
        });

        var fresh = Task.Run(async () =>
        {
            var ctx = await browser.NewContextAsync();
            try
            {
                var page = await ctx.NewPageAsync();
                await SendAndAwaitAsync(page, _fx.AgentIds[1], "HELLO_WORLD", "Hello, world!", timeoutMs: 60_000);
            }
            finally { await ctx.CloseAsync(); }
        });

        await Task.WhenAll(existing, fresh);
    }

    /// <summary>
    /// Sends LONG_RUNNING to an agent and verifies:
    ///   1. The streaming-message bubble appears immediately (spinner/live text visible).
    ///   2. After the stream completes the streaming-message bubble is gone.
    ///   3. The final completed message contains the sentinel [LONG_RUNNING_COMPLETE].
    /// This tests that the portal correctly transitions streaming → completed state
    /// and does not leave orphaned streaming indicators after a slow response.
    /// </summary>
    [SkippableFact]
    public async Task LongRunning_StreamIndicator_AppearsAndDisappears()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");
        try { await PlaywrightBootstrap.EnsureBrowserInstalledAsync(); }
        catch (Exception ex) { Skip.If(true, $"Playwright browser install unavailable: {ex.Message}"); }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var agentId = _fx.AgentIds[0];
        var url = $"{_fx.GatewayBaseUrl}/chat/{agentId}";
        var response = await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        Xunit.Assert.True(response!.Ok, $"GET {url} returned {response.Status}");

        var panel = page.Locator($"#{agentId}-conversation-panel");
        await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        var composer = panel.Locator("[data-testid='chat-composer']");
        await composer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await composer.FillAsync("LONG_RUNNING");
        await panel.Locator("[data-testid='chat-send']").ClickAsync();

        // Step 1: streaming-message bubble must appear.
        var streamingBubble = panel.Locator("[data-testid='streaming-message']");
        try
        {
            await streamingBubble.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 15_000,
            });
        }
        catch (TimeoutException)
        {
            Xunit.Assert.Fail($"Agent '{agentId}': streaming-message bubble never appeared during LONG_RUNNING stream.");
        }

        // Step 2: after stream completes the streaming bubble must disappear.
        try
        {
            await streamingBubble.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = 15_000,
            });
        }
        catch (TimeoutException)
        {
            Xunit.Assert.Fail($"Agent '{agentId}': streaming-message bubble still visible after LONG_RUNNING completed — orphaned spinner bug.");
        }

        // Step 3: final completed message contains the sentinel.
        var completedMsg = panel.Locator("[data-testid='message'][data-message-role='Assistant']")
            .Filter(new LocatorFilterOptions { HasTextString = "[LONG_RUNNING_COMPLETE]" });
        try
        {
            await completedMsg.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = 10_000,
            });
        }
        catch (TimeoutException)
        {
            Xunit.Assert.Fail($"Agent '{agentId}': completed message with '[LONG_RUNNING_COMPLETE]' not found after streaming indicator disappeared.");
        }
        await context.CloseAsync();
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private async Task SendAndAwaitAsync(IPage page, string agentId, string message, string expectedFragment, int timeoutMs)
    {
        var url = $"{_fx.GatewayBaseUrl}/chat/{agentId}";
        var response = await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000,
        });
        Xunit.Assert.NotNull(response);
        Xunit.Assert.True(response!.Ok, $"GET {url} returned {response.Status}");

        // Scope to this agent's panel — all agents render concurrently in the
        // multi-pane layout, so a global [data-testid] match is ambiguous.
        var panel = page.Locator($"#{agentId}-conversation-panel");
        await panel.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 30_000,
        });

        var composer = panel.Locator("[data-testid='chat-input'], [data-testid='chat-composer']");
        await composer.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });
        await composer.FillAsync(message);
        await panel.Locator("[data-testid='chat-send']").ClickAsync();

        // Wait for an assistant message containing the expected fragment.
        var assistantMatch = panel.Locator(
            "[data-testid='message'][data-message-role='Assistant'], [data-testid='streaming-message']")
            .Filter(new LocatorFilterOptions { HasTextString = expectedFragment });

        try
        {
            await assistantMatch.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = timeoutMs,
            });
        }
        catch (TimeoutException)
        {
            var snapshot = await page.ContentAsync();
            Xunit.Assert.Fail(
                $"Agent '{agentId}' did not produce assistant message containing '{expectedFragment}' " +
                $"within {timeoutMs}ms after sending '{message}'.\nHTML head:\n" +
                snapshot[..Math.Min(2000, snapshot.Length)]);
        }
    }
}
