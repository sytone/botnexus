using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SessionDebugPanelHistoryTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();

    public SessionDebugPanelHistoryTests()
    {
        _ctx.Services.AddSingleton(_restClient);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static SessionDebugSnapshotDto MakeSnapshot(
        string sessionId = "sess-1",
        SessionDebugHistoryDto? history = null) => new()
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
        Metadata = new Dictionary<string, object?>(),
        History = history
    };

    private static SessionDebugEntryDto MakeEntry(
        string role = "user",
        string content = "Hello world",
        string? toolName = null,
        bool isCompactionSummary = false,
        bool isCrashSentinel = false,
        bool isHistory = false) => new()
    {
        Role = role,
        Content = content,
        Timestamp = DateTimeOffset.UtcNow,
        ToolName = toolName,
        IsCompactionSummary = isCompactionSummary,
        IsCrashSentinel = isCrashSentinel,
        IsHistory = isHistory
    };

    [Fact]
    public void History_tab_button_is_rendered()
    {
        _restClient.GetSessionDebugAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot()));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='session-debug-tab-history']").ShouldNotBeNull());
    }

    [Fact]
    public async Task History_tab_shows_entries_with_role_badges()
    {
        var historyPage = new SessionDebugHistoryDto
        {
            TotalCount = 2,
            Offset = 0,
            Limit = 25,
            Entries = [
                MakeEntry("user", "Hello from user"),
                MakeEntry("assistant", "Hello from assistant")
            ]
        };
        _restClient.GetSessionDebugAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot(history: historyPage)));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tab-history']").ShouldNotBeNull());

        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-history']").Click());

        cut.WaitForAssertion(() =>
        {
            var entries = cut.FindAll("[data-testid='session-debug-entry']");
            entries.Count.ShouldBe(2);
            var roles = cut.FindAll("[data-testid='entry-role']");
            roles[0].TextContent.ShouldContain("user");
            roles[1].TextContent.ShouldContain("assistant");
        });
    }

    [Fact]
    public async Task History_tab_shows_empty_state_when_no_entries()
    {
        var emptyHistory = new SessionDebugHistoryDto
        {
            TotalCount = 0,
            Offset = 0,
            Limit = 25,
            Entries = []
        };
        _restClient.GetSessionDebugAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot(history: emptyHistory)));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tab-history']").ShouldNotBeNull());

        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-history']").Click());

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='session-debug-history-empty']").ShouldNotBeNull());
    }

    [Fact]
    public async Task History_tab_highlights_compaction_boundary_entries()
    {
        var historyPage = new SessionDebugHistoryDto
        {
            TotalCount = 1,
            Offset = 0,
            Limit = 25,
            Entries = [MakeEntry("system", "Summary: ...", isCompactionSummary: true)]
        };
        _restClient.GetSessionDebugAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot(history: historyPage)));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tab-history']").ShouldNotBeNull());

        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-history']").Click());

        cut.WaitForAssertion(() =>
        {
            var flag = cut.Find("[data-testid='entry-compaction-flag']");
            flag.ShouldNotBeNull();
            flag.TextContent.ShouldBe("compacted");
        });
    }

    [Fact]
    public async Task History_tab_paging_next_loads_next_page()
    {
        var page1 = new SessionDebugHistoryDto
        {
            TotalCount = 50,
            Offset = 0,
            Limit = 25,
            Entries = Enumerable.Range(0, 25).Select(i => MakeEntry("user", $"msg-{i}")).ToList()
        };
        var page2 = new SessionDebugHistoryDto
        {
            TotalCount = 50,
            Offset = 25,
            Limit = 25,
            Entries = Enumerable.Range(25, 25).Select(i => MakeEntry("assistant", $"msg-{i}")).ToList()
        };

        // First call returns page1, subsequent calls with offset=25 return page2
        _restClient.GetSessionDebugAsync(Arg.Any<string>(), Arg.Is<int>(o => o == 0), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot(history: page1)));
        _restClient.GetSessionDebugAsync(Arg.Any<string>(), Arg.Is<int>(o => o == 25), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot(history: page2)));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tab-history']").ShouldNotBeNull());

        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-history']").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-history-next']").ShouldNotBeNull());

        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-history-next']").Click());

        cut.WaitForAssertion(() =>
        {
            var roles = cut.FindAll("[data-testid='entry-role']");
            roles.Count.ShouldBe(25);
            roles[0].TextContent.ShouldContain("assistant");
        });
    }
}
