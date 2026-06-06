using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SessionDebugPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();

    public SessionDebugPanelTests()
    {
        _ctx.Services.AddSingleton(_restClient);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static SessionDebugSnapshotDto MakeSnapshot(
        string sessionId = "sess-1",
        string agentId = "agent-1",
        string status = "Active",
        string sessionType = "user-agent",
        string? conversationId = "conv-1",
        string? channelType = "signalr",
        int messageCount = 5,
        Dictionary<string, object?>? metadata = null) => new()
    {
        SessionId = sessionId,
        AgentId = agentId,
        Status = status,
        SessionType = sessionType,
        CreatedAt = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero),
        MessageCount = messageCount,
        ConversationId = conversationId,
        ChannelType = channelType,
        Metadata = metadata ?? new Dictionary<string, object?> { ["key1"] = "value1", ["key2"] = 42 }
    };

    [Fact]
    public void Renders_nothing_when_SessionId_is_null()
    {
        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, (string?)null));
        cut.Markup.ShouldNotContain("session-debug-panel");
    }

    [Fact]
    public void Shows_loading_state_while_fetching()
    {
        var tcs = new TaskCompletionSource<SessionDebugSnapshotDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => tcs.Task);

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.Markup.ShouldContain("Loading session");
        tcs.SetResult(null);
    }

    [Fact]
    public void Shows_overview_tab_with_session_fields_after_load()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot()));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='session-id']").TextContent.ShouldBe("sess-1");
            cut.Find("[data-testid='agent-id']").TextContent.ShouldBe("agent-1");
            cut.Find("[data-testid='status']").TextContent.ShouldBe("Active");
            cut.Find("[data-testid='session-type']").TextContent.ShouldBe("user-agent");
            cut.Find("[data-testid='message-count']").TextContent.ShouldBe("5");
            cut.Find("[data-testid='conversation-id']").TextContent.ShouldBe("conv-1");
            cut.Find("[data-testid='channel-type']").TextContent.ShouldBe("signalr");
        });
    }

    [Fact]
    public void Shows_metadata_tab_with_key_value_pairs()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot(
                metadata: new Dictionary<string, object?> { ["myKey"] = "myValue", ["count"] = 99 })));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tabs']"));

        // Switch to Metadata tab
        cut.Find("[data-testid='session-debug-tab-metadata']").Click();

        cut.WaitForAssertion(() =>
        {
            var keys = cut.FindAll("[data-testid='meta-key']");
            keys.Count.ShouldBe(2);
            keys.Select(k => k.TextContent).ShouldContain("myKey");
            keys.Select(k => k.TextContent).ShouldContain("count");
        });
    }

    [Fact]
    public void Shows_empty_state_when_metadata_is_empty()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot(
                metadata: new Dictionary<string, object?>())));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-tabs']"));

        cut.Find("[data-testid='session-debug-tab-metadata']").Click();

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='session-debug-metadata-empty']").TextContent.ShouldContain("No metadata"));
    }

    [Fact]
    public void Shows_not_found_error_when_api_returns_null()
    {
        _restClient.GetSessionDebugAsync("missing", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(null));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "missing"));

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='session-debug-error']").TextContent.ShouldContain("not found"));
    }

    [Fact]
    public void Shows_error_message_on_http_exception()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<SessionDebugSnapshotDto?>(new HttpRequestException("timeout")));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='session-debug-error']").TextContent.ShouldContain("Unable to load session"));
    }

    [Fact]
    public void Close_button_invokes_OnClose_callback()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot()));

        var closeFired = false;
        var cut = _ctx.Render<SessionDebugPanel>(p => p
            .Add(x => x.SessionId, "sess-1")
            .Add(x => x.OnClose, EventCallback.Factory.Create(this, () => closeFired = true)));

        cut.WaitForAssertion(() => cut.Find("[data-testid='session-debug-close']"));
        cut.Find("[data-testid='session-debug-close']").Click();

        closeFired.ShouldBeTrue();
    }

    [Fact]
    public void Null_conversationId_and_channelType_render_dash_placeholder()
    {
        _restClient.GetSessionDebugAsync("sess-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SessionDebugSnapshotDto?>(MakeSnapshot(conversationId: null, channelType: null)));

        var cut = _ctx.Render<SessionDebugPanel>(p => p.Add(x => x.SessionId, "sess-1"));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='conversation-id']").TextContent.ShouldBe("—");
            cut.Find("[data-testid='channel-type']").TextContent.ShouldBe("—");
        });
    }
}
