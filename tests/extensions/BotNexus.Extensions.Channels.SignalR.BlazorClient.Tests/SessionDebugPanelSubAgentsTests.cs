using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SessionDebugPanelSubAgentsTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();

    public SessionDebugPanelSubAgentsTests()
    {
        _ctx.Services.AddSingleton(_restClient);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static SessionDebugSnapshotDto MakeSnapshot(string sessionId = "sess-1") => new()
    {
        SessionId = sessionId,
        AgentId = "agent-1",
        Status = "Active",
        SessionType = "user-agent",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        MessageCount = 3,
        ConversationId = "conv-1",
        ChannelType = "signalr",
        Metadata = new Dictionary<string, object?>()
    };

    private static SubAgentInfo MakeSubAgent(
        string id = "sa-1",
        string status = "Running",
        string? model = "gpt-4o") => new()
    {
        SubAgentId = id,
        Status = status,
        Model = model
    };

    [Fact]
    public void SubAgents_tab_button_is_rendered()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot()));
        _restClient.ListSessionSubAgentsAsync("sess-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SubAgentInfo>>([]));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='session-debug-tab-sub-agents']").ShouldNotBeNull());
    }

    [Fact]
    public async Task SubAgents_tab_shows_empty_state_when_no_sub_agents()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot()));
        _restClient.ListSessionSubAgentsAsync("sess-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SubAgentInfo>>([]));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tab-sub-agents']").ShouldNotBeNull());

        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-sub-agents']").Click());

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='session-debug-subagents-empty']").ShouldNotBeNull());
    }

    [Fact]
    public async Task SubAgents_tab_shows_table_with_sub_agent_rows()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot()));
        _restClient.ListSessionSubAgentsAsync("sess-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SubAgentInfo>>(
                [MakeSubAgent("sa-1", "Completed", "claude-3"), MakeSubAgent("sa-2", "Running", null)]));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tab-sub-agents']").ShouldNotBeNull());

        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-sub-agents']").Click());

        cut.WaitForAssertion(() =>
        {
            var table = cut.Find("[data-testid='session-debug-subagents']");
            var ids = cut.FindAll("[data-testid='subagent-id']");
            ids.Count.ShouldBe(2);
            ids[0].TextContent.ShouldBe("sa-1");
            ids[1].TextContent.ShouldBe("sa-2");
        });
    }

    [Fact]
    public async Task SubAgents_tab_does_not_re_fetch_on_second_click()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot()));
        _restClient.ListSessionSubAgentsAsync("sess-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SubAgentInfo>>([MakeSubAgent()]));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tab-sub-agents']").ShouldNotBeNull());

        // First click
        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-sub-agents']").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-subagents']").ShouldNotBeNull());

        // Switch to overview then back to sub-agents
        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-overview']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-sub-agents']").Click());

        // Should only have called the API once
        await _restClient.Received(1).ListSessionSubAgentsAsync("sess-1", Arg.Any<CancellationToken>());
    }
}
