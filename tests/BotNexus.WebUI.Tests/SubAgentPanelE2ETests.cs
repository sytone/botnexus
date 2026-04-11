using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class SubAgentPanelE2ETests
{
    private const string AgentA = "agent-a";
    private readonly PlaywrightFixture _fixture;

    public SubAgentPanelE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task SubAgentSpawned_ShowsPanel()
    {
        var host = await OpenChatAsync();
        var sessionId = await host.SendMessageAsync("subagent-panel");
        await host.WaitForStreamingCompleteAsync();

        host.SubAgentManager.SetSubAgents(sessionId, CreateSubAgent(sessionId, "sa-1", SubAgentStatus.Running));
        await host.OpenAgentTimelineAsync(AgentA);

        await Assertions.Expect(host.Page.Locator("#subagent-panel")).ToBeVisibleAsync();
        await Assertions.Expect(host.Page.Locator("#subagent-list .subagent-item")).ToContainTextAsync("worker-sa-1");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SubAgentCompleted_UpdatesStatus()
    {
        var host = await OpenChatAsync();
        var sessionId = await host.SendMessageAsync("subagent-complete");
        await host.WaitForStreamingCompleteAsync();

        host.SubAgentManager.SetSubAgents(sessionId, CreateSubAgent(sessionId, "sa-done", SubAgentStatus.Completed));
        await host.OpenAgentTimelineAsync(AgentA);

        await Assertions.Expect(host.Page.Locator("#subagent-list .subagent-item .subagent-status-icon")).ToContainTextAsync("✅");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SubAgentFailed_UpdatesStatus()
    {
        var host = await OpenChatAsync();
        var sessionId = await host.SendMessageAsync("subagent-failed");
        await host.WaitForStreamingCompleteAsync();

        host.SubAgentManager.SetSubAgents(sessionId, CreateSubAgent(sessionId, "sa-fail", SubAgentStatus.Failed));
        await host.OpenAgentTimelineAsync(AgentA);

        await Assertions.Expect(host.Page.Locator("#subagent-list .subagent-item .subagent-status-icon")).ToContainTextAsync("❌");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task KillButton_SendsDeleteRequest()
    {
        var host = await OpenChatAsync();
        var sessionId = await host.SendMessageAsync("subagent-kill");
        await host.WaitForStreamingCompleteAsync();

        host.SubAgentManager.SetSubAgents(sessionId, CreateSubAgent(sessionId, "sa-kill", SubAgentStatus.Running));
        await host.OpenAgentTimelineAsync(AgentA);
        await host.Page.ClickAsync("#subagent-list .btn-kill-subagent");

        await WaitForAsync(
            () => Task.FromResult(host.SubAgentManager.KillRequests.Any(r => r.SubAgentId == "sa-kill" && r.RequestingSessionId == sessionId)),
            "Timed out waiting for sub-agent kill request.");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task NoSubAgents_PanelHidden()
    {
        var host = await OpenChatAsync();
        await host.SendMessageAsync("subagent-none");
        await host.WaitForStreamingCompleteAsync();

        await host.OpenAgentTimelineAsync(AgentA);
        await Assertions.Expect(host.Page.Locator("#subagent-panel")).ToBeHiddenAsync();
    }

    private static SubAgentInfo CreateSubAgent(string sessionId, string subAgentId, SubAgentStatus status)
        => new()
        {
            SubAgentId = subAgentId,
            ParentSessionId = sessionId,
            ChildSessionId = $"child-{subAgentId}",
            Name = $"worker-{subAgentId}",
            Task = "Investigate issue",
            Model = "gpt-4.1",
            Status = status,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
            CompletedAt = status == SubAgentStatus.Running ? null : DateTimeOffset.UtcNow,
            TurnsUsed = status == SubAgentStatus.Running ? 0 : 2,
            ResultSummary = status == SubAgentStatus.Running ? null : "Finished task."
        };

    private static async Task WaitForAsync(Func<Task<bool>> predicate, string message, int timeoutMs = 15000)
    {
        var start = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            if (await predicate())
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException(message);
    }

    private async Task<WebUiE2ETestHost> OpenChatAsync()
    {
        var host = await _fixture.CreatePageAsync();
        await host.OpenAgentTimelineAsync(AgentA);
        return host;
    }
}





