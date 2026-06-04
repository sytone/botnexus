using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Playwright-driven portal journey tests verifying real end-to-end user flows.
///
/// Uses stable data-testid selectors present in the current portal:
///   - [data-testid="chat-input"]        - the message textarea in ChatPanel
///   - [data-testid="chat-send"]         - the Send button
///   - [data-testid="chat-messages"]     - the messages scroll container
///   - [data-testid="message"]           - each completed message bubble
///   - [data-testid="streaming-message"] - the live in-progress bubble
///
/// NOTE: The old #{agentId}-conversation-panel scoping has been removed.
/// Each test navigates directly to /chat/{agentId} so the page contains
/// only that agent's chat panel — no ambiguity.
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
            WaitUntil = WaitUntilState.Load,
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
                Xunit.Assert.Fail(
                    $"Portal did not render agent id '{id}' within 30s.\n" +
                    $"HTML:\n{snapshot[..Math.Min(2000, snapshot.Length)]}");
            }
        }
    }

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
            await SendAndAwaitAsync(page, _fx.GatewayBaseUrl, agentId, "HELLO_WORLD", "Hello, world!", timeoutMs: 60_000);
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
                await SendAndAwaitAsync(page, _fx.GatewayBaseUrl, agentId, "MULTI_DELTA", "[MULTI_DELTA_COMPLETE]", timeoutMs: 90_000);
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

        // Seed an existing conversation on agent[0]
        var seedContext = await browser.NewContextAsync();
        var seedPage = await seedContext.NewPageAsync();
        await SendAndAwaitAsync(seedPage, _fx.GatewayBaseUrl, _fx.AgentIds[0], "HELLO_WORLD", "Hello, world!", timeoutMs: 60_000);
        await seedContext.CloseAsync();

        var existing = Task.Run(async () =>
        {
            var ctx = await browser.NewContextAsync();
            try
            {
                var page = await ctx.NewPageAsync();
                await SendAndAwaitAsync(page, _fx.GatewayBaseUrl, _fx.AgentIds[0], "MULTI_DELTA", "[MULTI_DELTA_COMPLETE]", timeoutMs: 90_000);
            }
            finally { await ctx.CloseAsync(); }
        });

        var fresh = Task.Run(async () =>
        {
            var ctx = await browser.NewContextAsync();
            try
            {
                var page = await ctx.NewPageAsync();
                await SendAndAwaitAsync(page, _fx.GatewayBaseUrl, _fx.AgentIds[1], "HELLO_WORLD", "Hello, world!", timeoutMs: 60_000);
            }
            finally { await ctx.CloseAsync(); }
        });

        await Task.WhenAll(existing, fresh);
    }

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
        var nav = await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000,
        });
        Xunit.Assert.True(nav!.Ok, $"GET {url} returned {nav.Status}");

        var composer = page.Locator("[data-testid='chat-input']").First;
        await composer.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });
        await composer.FillAsync("LONG_RUNNING");
        await page.Locator("[data-testid='chat-send']").First.ClickAsync();

        // Step 1: streaming-message bubble must appear
        var streamingBubble = page.Locator("[data-testid='streaming-message']").First;
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

        // Step 2: bubble must disappear after stream completes
        try
        {
            await streamingBubble.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = 30_000,
            });
        }
        catch (TimeoutException)
        {
            Xunit.Assert.Fail($"Agent '{agentId}': streaming-message bubble still visible after LONG_RUNNING completed.");
        }

        // Step 3: final message contains the sentinel
        var completedMsg = page.Locator("[data-testid='message']")
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
            Xunit.Assert.Fail($"Agent '{agentId}': completed message '[LONG_RUNNING_COMPLETE]' not found after streaming ended.");
        }

        await context.CloseAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task SendAndAwaitAsync(
        IPage page, string baseUrl, string agentId, string message, string expectedFragment, int timeoutMs)
    {
        var url = $"{baseUrl}/chat/{agentId}";
        var nav = await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000,
        });
        Xunit.Assert.NotNull(nav);
        Xunit.Assert.True(nav!.Ok, $"GET {url} returned {nav.Status}");

        var composer = page.Locator(".chat-panel-wrapper:not(.hidden) [data-testid='chat-input']").First;
        await composer.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });
        await composer.FillAsync(message);
        await page.Locator(".chat-panel-wrapper:not(.hidden) [data-testid='chat-send']").First.ClickAsync();

        var match = page.Locator("[data-testid='message'], [data-testid='streaming-message']")
            .Filter(new LocatorFilterOptions { HasTextString = expectedFragment });

        try
        {
            await match.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = timeoutMs,
            });
        }
        catch (TimeoutException)
        {
            var snapshot = await page.ContentAsync();
            Xunit.Assert.Fail(
                $"Agent '{agentId}' did not produce message containing '{expectedFragment}' " +
                $"within {timeoutMs}ms after sending '{message}'.\n" +
                $"HTML:\n{snapshot[..Math.Min(2000, snapshot.Length)]}");
        }
    }
}
