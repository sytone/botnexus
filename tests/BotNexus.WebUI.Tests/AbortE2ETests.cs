using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class AbortE2ETests
{
    private const string AgentA = "agent-a";
    private readonly PlaywrightFixture _fixture;

    public AbortE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task AbortButton_StopsStreaming()
    {
        await using var host = await StartStreamingAsync();
        await host.ClickAbortAsync();
        await host.WaitForSystemMessageAsync("Request aborted.");
        await host.WaitForAbortButtonHiddenAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task AbortButton_HidesProcessingBar()
    {
        await using var host = await StartStreamingAsync();
        await host.ClickAbortAsync();
        await host.WaitForProcessingBarHiddenAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task EscapeKey_AbortsWhenStreaming()
    {
        await using var host = await StartStreamingAsync();
        await host.PressEscapeAsync();
        await host.WaitForSystemMessageAsync("Request aborted.");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task AfterAbort_SendButtonReturnsToNormal()
    {
        await using var host = await StartStreamingAsync();
        await host.ClickAbortAsync();
        await host.WaitForAbortButtonHiddenAsync();
        await Assertions.Expect(host.Page.Locator("#btn-send")).ToContainTextAsync("Send");
        await Assertions.Expect(host.Page.Locator("#btn-send-mode")).ToBeHiddenAsync();
    }

    private async Task<WebUiE2ETestHost> StartStreamingAsync()
    {
        var host = await _fixture.CreatePageAsync();
        await host.OpenAgentTimelineAsync(AgentA);
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            InitialDelayMs = 4000,
            ContentDeltas = { "never-finished" }
        });
        await host.SendMessageAsync("abort-me");
        await host.WaitForAbortButtonVisibleAsync();
        return host;
    }
}





