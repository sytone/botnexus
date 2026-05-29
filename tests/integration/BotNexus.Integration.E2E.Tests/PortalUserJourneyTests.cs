using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Single Playwright-driven walk-through that opens the portal and verifies the
/// provisioned agents are visible. The richer multi-conversation, multi-agent,
/// parallel-streaming flow described in issue #598 is intentionally left as
/// followup placeholders below — landing this scaffold unblocks anyone wanting
/// to extend it without each PR re-doing the install/provisioning plumbing.
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

        var response = await page.GotoAsync(_fx.GatewayBaseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000,
        });
        Xunit.Assert.NotNull(response);
        Xunit.Assert.True(response!.Ok, $"GET / returned {response.Status}");

        // The portal is a Blazor WASM SPA — agents are fetched after hydration.
        // Use Playwright's text locator with a wait rather than racing a static
        // ContentAsync() snapshot taken right after NetworkIdle (the agent list
        // request may resolve after the SPA registers its first idle window).
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

    [SkippableFact]
    public async Task MixedExistingAndNewConversations_ConcurrentMessages()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");
        try { await PlaywrightBootstrap.EnsureBrowserInstalledAsync(); }
        catch (Exception ex) { Skip.If(true, $"Playwright browser install unavailable: {ex.Message}"); }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);

        // Seed an "existing" conversation on alpha by sending a HELLO_WORLD first.
        var seedContext = await browser.NewContextAsync();
        var seedPage = await seedContext.NewPageAsync();
        await SendAndAwaitAsync(seedPage, _fx.AgentIds[0], "HELLO_WORLD", "Hello, world!", timeoutMs: 60_000);
        await seedContext.CloseAsync();

        // In parallel:
        //   (a) reopen the portal on alpha (the existing conv is auto-selected) and send MULTI_DELTA
        //   (b) open a fresh context on bravo, which auto-creates a new conversation, send HELLO_WORLD
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
