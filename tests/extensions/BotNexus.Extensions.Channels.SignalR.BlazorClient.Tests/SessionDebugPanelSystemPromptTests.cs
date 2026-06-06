using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SessionDebugPanelSystemPromptTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();

    public SessionDebugPanelSystemPromptTests()
    {
        _ctx.Services.AddSingleton(_restClient);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static SessionDebugSnapshotDto MakeSnapshot(
        string? systemPrompt = null,
        DateTimeOffset? capturedAt = null) => new()
    {
        SessionId = "sess-1",
        AgentId = "agent-1",
        Status = "Active",
        SessionType = "user-agent",
        CreatedAt = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero),
        MessageCount = 3,
        ConversationId = "conv-1",
        ChannelType = "signalr",
        SystemPrompt = systemPrompt,
        SystemPromptCapturedAt = capturedAt,
        Metadata = new Dictionary<string, object?> { ["k"] = "v" }
    };

    // ── Tab presence ──────────────────────────────────────────────────────────

    [Fact]
    public void SystemPromptTab_IsPresent_InTabBar()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot()));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='session-debug-tab-system-prompt']").ShouldNotBeNull());
    }

    // ── Content with system prompt ────────────────────────────────────────────

    [Fact]
    public async Task SystemPromptTab_Click_ShowsPromptText()
    {
        const string expectedPrompt = "You are a helpful assistant.";
        var capturedAt = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);

        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot(expectedPrompt, capturedAt)));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tab-system-prompt']"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-system-prompt']").Click());

        cut.WaitForAssertion(() =>
        {
            var content = cut.Find("[data-testid='session-debug-system-prompt-content']");
            content.TextContent.ShouldContain(expectedPrompt);
        });
    }

    [Fact]
    public async Task SystemPromptTab_ShowsCapturedAtTimestamp_WhenSet()
    {
        var capturedAt = new DateTimeOffset(2026, 6, 5, 14, 30, 0, TimeSpan.Zero);

        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot("# System Prompt", capturedAt)));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tab-system-prompt']"));
        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-system-prompt']").Click());

        cut.WaitForAssertion(() =>
        {
            var ts = cut.Find("[data-testid='system-prompt-captured-at']");
            ts.ShouldNotBeNull();
        });
    }

    // ── Empty state ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SystemPromptTab_WhenSystemPromptIsNull_ShowsNotCapturedMessage()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot(systemPrompt: null)));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tab-system-prompt']"));
        await cut.InvokeAsync(() => cut.Find("[data-testid='session-debug-tab-system-prompt']").Click());

        cut.WaitForAssertion(() =>
        {
            var empty = cut.Find("[data-testid='session-debug-system-prompt-empty']");
            empty.ShouldNotBeNull();
            empty.TextContent.ShouldContain("not yet captured");
        });
    }
}
