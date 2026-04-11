using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class SteerAndFollowUpE2ETests
{
    private const string AgentA = "agent-a";
    private readonly PlaywrightFixture _fixture;

    public SteerAndFollowUpE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task DuringStreaming_SendButtonBecomesSteer()
    {
        await using var host = await StartStreamingAsync();
        await Assertions.Expect(host.Page.Locator("#btn-send")).ToContainTextAsync("🧭 Steer");
        await Assertions.Expect(host.Page.Locator("#btn-send-mode")).ToBeVisibleAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SteerMessage_SentViaHub()
    {
        await using var host = await StartStreamingAsync();
        await host.Page.FillAsync("#chat-input", "steer now");
        await host.Page.ClickAsync("#btn-send");
        await WaitForDispatchContentAsync(host, "steer now");
        await Assertions.Expect(host.Page.Locator("#chat-messages .message.system-msg")).ToContainTextAsync("🧭 Steering: steer now");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SteerIndicator_ShownBriefly()
    {
        await using var host = await StartStreamingAsync();
        await host.Page.FillAsync("#chat-input", "steer indicator");
        await host.Page.ClickAsync("#btn-send");
        await Assertions.Expect(host.Page.Locator("#steer-indicator")).ToBeVisibleAsync();
        await Assertions.Expect(host.Page.Locator("#steer-indicator")).ToBeHiddenAsync(new() { Timeout = 5000 });
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SendModeToggle_SwitchesToFollowUp()
    {
        await using var host = await StartStreamingAsync();
        await host.Page.ClickAsync("#btn-send-mode");
        await Assertions.Expect(host.Page.Locator("#btn-send")).ToContainTextAsync("📨 Follow-up");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task FollowUpMessage_QueuesAndShowsCount()
    {
        await using var host = await StartStreamingAsync();
        await host.Page.ClickAsync("#btn-send-mode");
        await host.Page.FillAsync("#chat-input", "queued follow up");
        await host.Page.ClickAsync("#btn-send");

        await Assertions.Expect(host.Page.Locator("#queue-status")).ToBeVisibleAsync();
        await Assertions.Expect(host.Page.Locator("#queue-count")).ToContainTextAsync("1 message queued");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task QueueCount_DecrementsOnMessageStart()
    {
        await using var host = await StartStreamingAsync();
        await host.Page.ClickAsync("#btn-send-mode");
        await host.Page.FillAsync("#chat-input", "queue decrement");
        await host.Page.ClickAsync("#btn-send");
        await Assertions.Expect(host.Page.Locator("#queue-count")).ToContainTextAsync("1 message queued");

        await Assertions.Expect(host.Page.Locator("#queue-status")).ToBeHiddenAsync(new() { Timeout = 15000 });
    }

    private async Task<WebUiE2ETestHost> StartStreamingAsync()
    {
        var host = await _fixture.CreatePageAsync();
        await host.OpenAgentTimelineAsync(AgentA);
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            InitialDelayMs = 5000,
            DelayBetweenDeltasMs = 1200,
            ContentDeltas = { "first ", "second" }
        });
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            InitialDelayMs = 150,
            ContentDeltas = { "followup-response" }
        });

        await host.SendMessageAsync("start streaming");
        await host.WaitForAbortButtonVisibleAsync();
        await host.Page.FillAsync("#chat-input", "mid-stream");
        return host;
    }

    private static async Task WaitForDispatchCountAsync(WebUiE2ETestHost host, string kind, int expected, int timeoutMs = 15000)
    {
        var start = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            if (host.Supervisor.Dispatches.Count(d => d.Kind == kind) >= expected)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for {expected} {kind} dispatches.");
    }

    private static async Task WaitForDispatchContentAsync(WebUiE2ETestHost host, string content, int timeoutMs = 15000)
    {
        var start = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            if (host.Supervisor.Dispatches.Any(d => d.Content == content))
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for dispatch content '{content}'.");
    }
}





