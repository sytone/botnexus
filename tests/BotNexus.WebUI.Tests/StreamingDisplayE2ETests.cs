using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class StreamingDisplayE2ETests
{
    private const string AgentA = "agent-a";
    private readonly PlaywrightFixture _fixture;

    public StreamingDisplayE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task ContentDelta_AppendsTextProgressively()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            InitialDelayMs = 50,
            DelayBetweenDeltasMs = 1200,
            ContentDeltas = { "hello ", "world" }
        });

        await host.SendMessageAsync("progressive");
        await Assertions.Expect(host.Page.Locator("#chat-messages .delta-content")).ToContainTextAsync("hello ");
        (await host.Page.Locator("#chat-messages .delta-content").InnerTextAsync()).Should().NotContain("world");
        await Assertions.Expect(host.Page.Locator("#chat-messages .delta-content")).ToContainTextAsync("hello world");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task MessageEnd_FinalizesWithMarkdown()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ContentDeltas = { "**bold** text" }
        });

        await host.SendMessageAsync("markdown");
        await host.WaitForStreamingCompleteAsync();
        await Assertions.Expect(host.Page.Locator("#chat-messages .message.assistant .msg-content strong")).ToContainTextAsync("bold");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ProcessingBar_VisibleDuringStreaming()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            InitialDelayMs = 2500,
            ContentDeltas = { "slow" }
        });

        await host.SendMessageAsync("processing-visible");
        await host.WaitForProcessingBarAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ProcessingBar_HiddenAfterMessageEnd()
    {
        var host = await OpenChatAsync();
        await host.SendMessageAsync("processing-hidden");
        await host.WaitForStreamingCompleteAsync();
        await host.WaitForProcessingBarHiddenAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task AbortButton_VisibleDuringStreaming()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            InitialDelayMs = 2500,
            ContentDeltas = { "slow-abort-visible" }
        });

        await host.SendMessageAsync("abort-visible");
        await host.WaitForAbortButtonVisibleAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task AbortButton_HiddenAfterMessageEnd()
    {
        var host = await OpenChatAsync();
        await host.SendMessageAsync("abort-hidden");
        await host.WaitForStreamingCompleteAsync();
        await host.WaitForAbortButtonHiddenAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task MessageEnd_ShowsFooterWithToolCount()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ContentDeltas = { "tool-result" },
            ToolCalls =
            {
                new StreamToolCall("tool-1", "functions.search", new Dictionary<string, object?> { ["query"] = "abc" }, "ok", false, 50, 50)
            }
        });

        await host.SendMessageAsync("tool-footer");
        await host.WaitForStreamingCompleteAsync();
        await Assertions.Expect(host.Page.Locator("#chat-messages .message.assistant .msg-footer")).ToContainTextAsync("1 tool call");
    }

    private async Task<WebUiE2ETestHost> OpenChatAsync()
    {
        var host = await _fixture.CreatePageAsync();
        await host.OpenAgentTimelineAsync(AgentA);
        return host;
    }
}





