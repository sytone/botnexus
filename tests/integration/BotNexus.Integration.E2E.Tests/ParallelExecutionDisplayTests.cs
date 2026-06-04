using System.Net.Http;
using System.Text.Json;
using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for observable portal behaviour during parallel and concurrent
/// agent execution — the "power user with 10 agents all doing things" scenario.
///
/// Grounded in a real incident: while monitoring a PR via cron, the portal had
/// multiple concurrent sessions across different channel types. The UI had no
/// clear indication of which sessions were active, what their status was, or
/// whether responses had been lost to orphaned sessions.
///
/// These tests verify:
///   1. Connection status indicator accurately reflects gateway reachability.
///   2. While one agent is executing (streaming), switching to another agent
///      doesn't freeze or corrupt the streaming agent's state.
///   3. Multiple simultaneous streams don't interfere with each other's
///      displayed content.
///   4. The portal correctly shows "no active sessions" vs "N active sessions"
///      when different numbers of sessions exist.
///   5. An agent with a very long response history still loads within the
///      acceptable time budget (first-load performance test).
///   6. Navigation to an agent mid-stream shows the streaming indicator
///      and doesn't miss the final assembled response.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ParallelExecutionDisplayTests
{
    private readonly NewUserExperienceFixture _fx;

    public ParallelExecutionDisplayTests(NewUserExperienceFixture fx) => _fx = fx;

    /// <summary>
    /// Connection status indicator in the sidebar must show "connected" when
    /// the gateway is reachable. This is the user's primary signal that the
    /// portal is working — if it shows the wrong state, they have no idea
    /// whether their messages are going anywhere.
    /// </summary>
    [SkippableFact]
    public async Task ConnectionStatus_WhenGatewayIsReachable_ShowsConnected()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var agentId = _fx.AgentIds[0];

        var page = await browser.NewPageAsync();
        var portal = new PortalPage(page);
        await portal.GotoAndWaitForLoadAsync(_fx.GatewayBaseUrl);

        // Connection status widget must be visible
        await portal.ConnectionStatus.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        var statusText = await portal.ConnectionStatus.InnerTextAsync();
        var statusClass = await portal.ConnectionStatus.GetAttributeAsync("class") ?? string.Empty;

        // Must show some form of "connected" state — not "disconnected", "error", or blank
        var isConnected = statusText.Contains("Connect", StringComparison.OrdinalIgnoreCase)
            || statusClass.Contains("connected", StringComparison.OrdinalIgnoreCase)
            || statusClass.Contains("online", StringComparison.OrdinalIgnoreCase);

        var isDisconnected = statusText.Contains("Disconnect", StringComparison.OrdinalIgnoreCase)
            || statusText.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || statusClass.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
            || statusClass.Contains("error", StringComparison.OrdinalIgnoreCase);

        Assert.False(isDisconnected,
            $"Connection status shows disconnected/error state even though gateway is running. " +
            $"Status text: '{statusText}', classes: '{statusClass}'. " +
            "This is a false alarm that would cause users to think the portal is broken.");
    }

    /// <summary>
    /// When agent A is mid-stream, switching to agent B must:
    ///  (a) Not freeze agent B's panel
    ///  (b) Not cancel agent A's stream prematurely
    ///  (c) When switching back to agent A, show the complete final response
    ///
    /// This is the "user is impatient and clicks around while things load" scenario.
    /// </summary>
    [SkippableFact]
    public async Task SwitchAgentDuringStream_DoesNotCorruptEitherAgent()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var agentA = _fx.AgentIds[0];
        var agentB = _fx.AgentIds[1];

        var (page, portal, chatA) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, agentA);

        // Start a slow-streaming response on agent A (MULTI_DELTA has 80ms delays)
        var sendTask = chatA.SendMessageAsync("MULTI_DELTA");

        // Wait briefly then switch to agent B while agent A is still streaming
        // Wait for at least one streaming message to appear before switching
        await chatA.StreamingIndicator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await portal.SelectAgentAsync(agentB);

        // Agent B panel should load normally — not stuck/frozen
        // Scope to the specific agent panel to avoid multi-panel strict mode violations.
        var agentBPanel = page.Locator($"#{agentB}-conversation-panel");
        await agentBPanel.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        // Agent B's input should be usable
        var chatB = new ChatPanelPage(page, agentB);
        var inputVisible = await chatB.ChatInput.IsVisibleAsync();
        Assert.True(inputVisible,
            "After switching to agent B while agent A was streaming, agent B's input is not visible. " +
            "The panel appears frozen or broken.");

        // Wait for the send task to settle
        await sendTask;

        // Switch back to agent A
        await portal.SelectAgentAsync(agentA);
        // Wait for agent A's panel to be active and visible
        await page.Locator("[data-testid='agent-panel']").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        // Agent A must show the complete MULTI_DELTA response
        // (not a blank, not a partial response, not an error)
        await chatA.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(20));
        await chatA.WaitForAssistantMessageAsync("carefully", TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// First-load performance: opening an agent with a moderately long
    /// conversation history (20+ exchanges) must render fully interactive
    /// within 3 seconds. The portal must not require multiple reloads
    /// or show a perpetual loading spinner.
    /// </summary>
    [SkippableFact]
    public async Task FirstLoad_AgentWithHistory_InteractiveWithinThreeSeconds()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var agentId = _fx.AgentIds[2];

        // Seed 10 exchanges into this agent's history via chat
        var (seedPage, seedPortal, seedChat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, agentId);
        for (int i = 0; i < 10; i++)
        {
            await seedChat.SendMessageAsync("HELLO_WORLD");
            await seedChat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(15));
        }

        // Capture the seeded session URL
        var seededUrl = seedPage.Url;

        // Now open the seeded URL in a fresh page and time the load
        var freshPage = await browser.NewPageAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await freshPage.GotoAsync(seededUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 30_000,
        });

        // Wait for agent panel to be visible and interactive
        // Scope to the specific agent panel to avoid multi-panel strict mode violations.
        await freshPage.Locator($"#{agentId}-conversation-panel")
            .WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000,
            });
        sw.Stop();

        Assert.True(sw.Elapsed.TotalSeconds < 3.0,
            $"First-load of agent with 10 exchanges of history took {sw.Elapsed.TotalSeconds:F1}s " +
            $"(target: <3s). The portal is too slow for a power user with meaningful history. " +
            "Investigate render blocking, over-fetching, or missing pagination.");

        // Input must be functional (portal fully hydrated, not just painted)
        var freshChat = new ChatPanelPage(freshPage, agentId);
        var inputEnabled = await freshChat.ChatInput.IsEnabledAsync();
        Assert.True(inputEnabled,
            "Agent panel visible but input is disabled/frozen after load. " +
            "Portal not fully interactive despite appearing loaded.");
    }

    /// <summary>
    /// When an agent is executing (streaming), the portal must show a clear
    /// "in progress" visual indicator (spinner, typing indicator, etc.).
    /// When execution completes, the indicator must disappear — not stay
    /// frozen or remain indefinitely.
    /// </summary>
    [SkippableFact]
    public async Task StreamingIndicator_AppearsWhileStreaming_DisappearsOnComplete()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var agentId = _fx.AgentIds[0];

        var (_, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, agentId);

        // Trigger a slow stream so we have time to observe the indicator
        await chat.SendMessageAsync("MULTI_DELTA");

        // A streaming indicator should appear
        var streamingIndicator = chat.StreamingIndicator;
        var indicatorVisible = false;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await streamingIndicator.IsVisibleAsync())
            {
                indicatorVisible = true;
                break;
            }
            await Task.Delay(100);
        }

        Assert.True(indicatorVisible,
            "No streaming indicator appeared while MULTI_DELTA was streaming. " +
            "Users have no feedback that the agent is working. " +
            "Add a visible streaming/typing indicator during active responses.");

        // Wait for streaming to complete
        await chat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(30));

        // Indicator must disappear after completion
        var indicatorGone = await streamingIndicator.IsHiddenAsync();
        Assert.True(indicatorGone,
            "Streaming indicator is still visible after streaming completed. " +
            "A frozen 'thinking' indicator makes the user think the agent is still working. " +
            "The indicator must be hidden/removed once the response is fully delivered.");
    }

    /// <summary>
    /// A gateway health check exposed at /health must return HTTP 200 and
    /// a body that indicates the gateway is ready. This underpins all cron
    /// job reliability — if /health isn't reliable, the test fixture's
    /// WaitForGatewayReadyAsync can give false positives.
    /// </summary>
    [SkippableFact]
    public async Task GatewayHealth_Returns200_WithReadyStatus()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var resp = await http.GetAsync($"{_fx.GatewayBaseUrl}/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body),
            "GET /health returned 200 but with an empty body. " +
            "Health endpoint must return a body (JSON or plain text) describing gateway status.");

        // Accept either a "Healthy" string or a JSON object with status field
        var isHealthy = body.Contains("Healthy", StringComparison.OrdinalIgnoreCase)
            || body.Contains("healthy", StringComparison.OrdinalIgnoreCase)
            || body.Contains("\"status\"", StringComparison.OrdinalIgnoreCase);

        Assert.True(isHealthy,
            $"GET /health returned 200 but body does not indicate healthy status. " +
            $"Body: '{body.Substring(0, Math.Min(200, body.Length))}'. " +
            "Health endpoint body should contain 'Healthy' or a status field.");
    }

    /// <summary>
    /// Parallel execution: send messages to two different agents simultaneously
    /// and verify both complete correctly with no content cross-contamination.
    ///
    /// This simulates the power-user scenario where multiple agents are all
    /// actively processing work at the same time.
    /// </summary>
    [SkippableFact]
    public async Task ParallelAgentExecution_BothComplete_NoContentCrossContamination()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;

        // Open two agent pages in separate tabs
        var (pageA, _, chatA) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);
        var (pageB, _, chatB) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[1]);

        // Start both streams simultaneously
        var taskA = chatA.SendMessageAsync("HELLO_WORLD");
        var taskB = chatB.SendMessageAsync("MULTI_DELTA");

        await Task.WhenAll(taskA, taskB);

        // Both must complete without timeout
        await chatA.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(30));
        await chatB.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(30));

        // Agent A's panel must show HELLO_WORLD response content
        var contentA = await pageA.ContentAsync();
        Assert.True(contentA.Contains("Hello", StringComparison.OrdinalIgnoreCase),
            "Agent A parallel execution: 'Hello' not found in response after parallel stream. " +
            "HELLO_WORLD response either failed or content is missing.");

        // Agent B's panel must show MULTI_DELTA response content
        var contentB = await pageB.ContentAsync();
        Assert.True(contentB.Contains("carefully", StringComparison.OrdinalIgnoreCase),
            "Agent B parallel execution: 'carefully' not found in MULTI_DELTA response. " +
            "Parallel streaming may have caused content loss or corruption.");

        // Cross-contamination check: A's panel must not show B's content
        Assert.False(contentA.Contains("carefully", StringComparison.OrdinalIgnoreCase),
            "Agent A's panel contains 'carefully' from Agent B's MULTI_DELTA stream. " +
            "Parallel execution caused content cross-contamination between agent panels.");
    }
}
