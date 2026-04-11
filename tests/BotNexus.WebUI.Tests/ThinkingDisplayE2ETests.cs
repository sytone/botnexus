using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class ThinkingDisplayE2ETests
{
    private const string AgentA = "agent-a";
    private readonly PlaywrightFixture _fixture;

    public ThinkingDisplayE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task ThinkingDelta_ShowsThinkingBlock()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ThinkingDelta = "considering options",
            InitialDelayMs = 300,
            DelayBetweenDeltasMs = 1500,
            ContentDeltas = { "answer" }
        });

        await host.SendMessageAsync("thinking-block");
        await Assertions.Expect(host.Page.Locator("#chat-messages .thinking-block .thinking-pre")).ToContainTextAsync("considering options");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ThinkingBlock_ShowsCharCount()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ThinkingDelta = "1234567890",
            InitialDelayMs = 300,
            DelayBetweenDeltasMs = 2000,
            ContentDeltas = { "partial", "complete" }
        });

        await host.SendMessageAsync("thinking-count");
        await Assertions.Expect(host.Page.Locator("#chat-messages .thinking-block .thinking-stats")).ToContainTextAsync("10 chars");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ThinkingBlock_CollapsibleToggle()
    {
        var host = await OpenChatAsync();
        await SetThinkingVisibilityAsync(host, true);
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ThinkingDelta = "expand me",
            InitialDelayMs = 300,
            DelayBetweenDeltasMs = 1800,
            ContentDeltas = { "streaming" }
        });

        await host.SendMessageAsync("toggle-thinking-block");
        var block = host.Page.Locator("#chat-messages .thinking-block").First;
        await host.Page.ClickAsync("#chat-messages .thinking-block .thinking-toggle");
        (await block.GetAttributeAsync("class")).Should().NotContain("collapsed");
        await host.Page.ClickAsync("#chat-messages .thinking-block .thinking-toggle");
        (await block.GetAttributeAsync("class")).Should().Contain("collapsed");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ThinkingBlock_AutoCollapsesOnContentDelta()
    {
        var host = await OpenChatAsync();
        await SetThinkingVisibilityAsync(host, true);
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ThinkingDelta = "expanded then collapse",
            InitialDelayMs = 300,
            DelayBetweenDeltasMs = 1500,
            ContentDeltas = { "first delta", "second delta" }
        });

        await host.SendMessageAsync("auto-collapse");
        var block = host.Page.Locator("#chat-messages .thinking-block").First;
        (await block.GetAttributeAsync("class")).Should().Contain("collapsed");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ThinkingToggle_ShowsHidesAllBlocks()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ThinkingDelta = "toggle all",
            InitialDelayMs = 300,
            DelayBetweenDeltasMs = 2000,
            ContentDeltas = { "first", "second" }
        });

        await host.SendMessageAsync("header-toggle");
        var block = host.Page.Locator("#chat-messages .thinking-block").First;
        (await block.GetAttributeAsync("class")).Should().Contain("collapsed");

        await SetThinkingVisibilityAsync(host, true);
        (await block.GetAttributeAsync("class")).Should().NotContain("collapsed");

        await SetThinkingVisibilityAsync(host, false);
        (await block.GetAttributeAsync("class")).Should().Contain("collapsed");
    }

    private async Task<WebUiE2ETestHost> OpenChatAsync()
    {
        var host = await _fixture.CreatePageAsync();
        await host.OpenAgentTimelineAsync(AgentA);
        return host;
    }
private static Task SetThinkingVisibilityAsync(WebUiE2ETestHost host, bool visible)
        => host.Page.EvaluateAsync(
            @"(isVisible) => {
                const toggle = document.querySelector('#toggle-thinking');
                if (!toggle) return;
                toggle.checked = isVisible;
                toggle.dispatchEvent(new Event('change', { bubbles: true }));
            }",
            visible);
}



