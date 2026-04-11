using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
public sealed class AbortE2ETests : IAsyncLifetime
{
    private const string AgentA = "agent-a";
    private WebUiE2ETestHost? _host;

    public async Task InitializeAsync()
    {
        _host = await WebUiE2ETestHost.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task AbortButton_StopsStreaming()
    {
        var host = await StartStreamingAsync();
        await host.ClickAbortAsync();
        await host.WaitForSystemMessageAsync("Request aborted.");
        await host.WaitForAbortButtonHiddenAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task AbortButton_HidesProcessingBar()
    {
        var host = await StartStreamingAsync();
        await host.ClickAbortAsync();
        await host.WaitForProcessingBarHiddenAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task EscapeKey_AbortsWhenStreaming()
    {
        var host = await StartStreamingAsync();
        await host.PressEscapeAsync();
        await host.WaitForSystemMessageAsync("Request aborted.");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task AfterAbort_SendButtonReturnsToNormal()
    {
        var host = await StartStreamingAsync();
        await host.ClickAbortAsync();
        await host.WaitForAbortButtonHiddenAsync();
        await Assertions.Expect(host.Page.Locator("#btn-send")).ToContainTextAsync("Send");
        await Assertions.Expect(host.Page.Locator("#btn-send-mode")).ToBeHiddenAsync();
    }

    private async Task<WebUiE2ETestHost> StartStreamingAsync()
    {
        var host = GetHost();
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

    private WebUiE2ETestHost GetHost()
        => _host ?? throw new InvalidOperationException("Playwright host was not initialized.");
}
