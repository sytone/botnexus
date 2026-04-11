using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class ToolCallDisplayE2ETests
{
    private const string AgentA = "agent-a";
    private readonly PlaywrightFixture _fixture;

    public ToolCallDisplayE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task ToolStart_ShowsRunningBadge()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ToolCalls =
            {
                new StreamToolCall("tc-run", "functions.search", new Dictionary<string, object?> { ["query"] = "abc" }, "ok", false, 100, 2200)
            },
            ContentDeltas = { "done" }
        });

        await host.SendMessageAsync("tool-running");
        await Assertions.Expect(host.Page.Locator(".tool-call[data-call-id='tc-run'] .tool-status-badge")).ToContainTextAsync("Running");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ToolEnd_ShowsDoneBadge()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ToolCalls =
            {
                new StreamToolCall("tc-done", "functions.search", new Dictionary<string, object?> { ["query"] = "abc" }, "ok")
            },
            ContentDeltas = { "done" }
        });

        await host.SendMessageAsync("tool-done");
        await host.WaitForStreamingCompleteAsync();
        await Assertions.Expect(host.Page.Locator(".tool-call[data-call-id='tc-done'] .tool-status-badge")).ToContainTextAsync("✅ Done");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ToolEnd_Error_ShowsErrorBadge()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ToolCalls =
            {
                new StreamToolCall("tc-error", "functions.fail", new Dictionary<string, object?>(), "boom", true)
            },
            ContentDeltas = { "done" }
        });

        await host.SendMessageAsync("tool-error");
        await host.WaitForStreamingCompleteAsync();
        await Assertions.Expect(host.Page.Locator(".tool-call[data-call-id='tc-error'] .tool-status-badge")).ToContainTextAsync("❌ Error");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ToolCall_ClickExpandsInspector()
    {
        var host = await OpenChatAsync();
        await SetToolsVisibilityAsync(host, true);
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ToolCalls = { new StreamToolCall("tc-expand", "functions.search", new Dictionary<string, object?> { ["query"] = "abc" }, "ok") },
            ContentDeltas = { "done" }
        });

        await host.SendMessageAsync("tool-expand");
        await host.WaitForStreamingCompleteAsync();
        var tool = host.Page.Locator(".tool-call[data-call-id='tc-expand']").First;
        await tool.ClickAsync();
        (await tool.GetAttributeAsync("class")).Should().Contain("expanded");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ToolToggle_ShowsHidesAllTools()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ToolCalls = { new StreamToolCall("tc-toggle", "functions.search", new Dictionary<string, object?> { ["query"] = "abc" }, "ok") },
            ContentDeltas = { "done" }
        });

        await host.SendMessageAsync("tool-toggle");
        await host.WaitForStreamingCompleteAsync();
        var tool = host.Page.Locator(".tool-call[data-call-id='tc-toggle']").First;

        (await tool.GetAttributeAsync("class")).Should().Contain("hidden");
        await SetToolsVisibilityAsync(host, true);
        (await tool.GetAttributeAsync("class")).Should().NotContain("hidden");
        await SetToolsVisibilityAsync(host, false);
        (await tool.GetAttributeAsync("class")).Should().Contain("hidden");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ToolCalls_HiddenByDefault()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            ToolCalls = { new StreamToolCall("tc-hidden", "functions.search", new Dictionary<string, object?> { ["query"] = "abc" }, "ok") },
            ContentDeltas = { "done" }
        });

        await host.SendMessageAsync("tool-hidden");
        await host.WaitForStreamingCompleteAsync();
        (await host.Page.Locator(".tool-call[data-call-id='tc-hidden']").First.GetAttributeAsync("class")).Should().Contain("hidden");
    }

    private async Task<WebUiE2ETestHost> OpenChatAsync()
    {
        var host = await _fixture.CreatePageAsync();
        await host.OpenAgentTimelineAsync(AgentA);
        return host;
    }
private static Task SetToolsVisibilityAsync(WebUiE2ETestHost host, bool visible)
        => host.Page.EvaluateAsync(
            @"(isVisible) => {
                const toggle = document.querySelector('#toggle-tools');
                if (!toggle) return;
                toggle.checked = isVisible;
                toggle.dispatchEvent(new Event('change', { bubbles: true }));
            }",
            visible);
}





