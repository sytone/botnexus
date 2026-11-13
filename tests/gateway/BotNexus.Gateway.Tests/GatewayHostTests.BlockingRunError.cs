using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgentUserMessage = BotNexus.Gateway.Abstractions.Models.AgentUserMessage;

namespace BotNexus.Gateway.Tests;

// Blocking branch (no streaming adapter): a Failed run records its error; nothing else changes.
public sealed partial class GatewayHostTests
{
    [Theory]
    [InlineData("api", "upstream service unavailable", null, "upstream service unavailable")]
    [InlineData("webhook", "upstream service unavailable", null, "upstream service unavailable")]
    [InlineData("api", "upstream service unavailable", "context compaction failed", "upstream service unavailable")]
    [InlineData("api", null, "context compaction failed", "context compaction failed")]
    [InlineData("api", null, null, "The agent run failed but supplied no detail.")]
    public async Task DispatchAsync_BlockingRunSettlesFailed_RecordsTheErrorAfterTheReply(
        string channelType, string? terminalError, string? completionDetail, string expectedDetail)
    {
        var turn = await RunBlockingTurnAsync(channelType, FailedResponse(string.Empty, terminalError, completionDetail));

        var history = turn.Session.GetHistorySnapshot();
        history[^1].Role.ShouldBe(MessageRole.System);
        history[^1].Content.ShouldBe($"Agent stream error: {expectedDetail}");
        history[^1].AgentRunId.ShouldBe(BlockingRunId);
        history.Count(IsErrorRow).ShouldBe(1);
        turn.FinalSaveHeldErrorRow.ShouldBeTrue("the row must be persisted, not only added in memory");

        // Everything else stays as it was: the (empty) reply row, fan-out, sentinel and events.
        history[^2].Role.ShouldBe(MessageRole.Assistant);
        history[^2].Content.ShouldBeEmpty();
        turn.FannedOut.ShouldBe(string.Empty);
        history.ShouldNotContain(e => e.IsCrashSentinel);
        turn.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_BlockingRunSettlesFailedWithNoReply_RecordsTheErrorWithoutAReplyRow()
    {
        var turn = await RunBlockingTurnAsync("api", FailedResponse("NO_REPLY", "upstream service unavailable", null));

        var history = turn.Session.GetHistorySnapshot();
        history[^1].Content.ShouldBe("Agent stream error: upstream service unavailable");
        turn.FinalSaveHeldErrorRow.ShouldBeTrue();
        history.ShouldNotContain(e => e.Role == MessageRole.Assistant, "NO_REPLY stores no reply, as before");
        turn.FannedOut.ShouldBeNull();
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Parked")]
    [InlineData("IncompleteWithoutStopReason")]
    [InlineData("Working")]
    [InlineData(null)]
    public async Task DispatchAsync_BlockingRunSettlesWithoutFailure_StoresTheReplyAsBefore(string? status)
    {
        var turn = await RunBlockingTurnAsync("api", new AgentResponse
        {
            Content = "done",
            Completion = status is null ? null : CompletionWithStatus(status, detail: null)
        });

        var history = turn.Session.GetHistorySnapshot();
        history[^1].Role.ShouldBe(MessageRole.Assistant);
        history[^1].Content.ShouldBe("done");
        history.ShouldNotContain(e => IsErrorRow(e));
        turn.FinalSaveHeldErrorRow.ShouldBeFalse();
        history.ShouldNotContain(e => e.IsCrashSentinel);
        turn.Published.ShouldBeEmpty();
        turn.FannedOut.ShouldBe("done");
    }

    private static bool IsErrorRow(SessionEntry entry) => entry.Content.StartsWith("Agent stream error:", StringComparison.Ordinal);

    private static readonly AgentRunId BlockingRunId = AgentRunId.From("run-blocking");

    private static AgentResponse FailedResponse(string content, string? terminalError, string? completionDetail)
        => new() { AgentRunId = BlockingRunId, Content = content, TerminalError = terminalError, Completion = CompletionWithStatus("Failed", completionDetail) };

    private static RunCompletionSignal CompletionWithStatus(string status, string? detail)
        => new(status, [], StopReason: null, detail, Evidence: null, ContinuationOwner: null, WakeCondition: null, ContinuationAttempts: 0);

    private sealed record BlockingTurn(GatewaySession Session, List<SessionLifecycleEventType> Published, string? FannedOut, bool FinalSaveHeldErrorRow);

    private static async Task<BlockingTurn> RunBlockingTurnAsync(string channelType, AgentResponse response)
    {
        var agentId = AgentId.From("agent-blocking");
        var sessionId = SessionId.From("session-blocking");
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>())).ReturnsAsync([agentId.Value]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(agentId);
        handle.SetupGet(h => h.SessionId).Returns(sessionId);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(handle.Object);

        // The blocking branch's final save is the fenced one; record what it held at save time.
        var session = new GatewaySession { SessionId = sessionId, AgentId = agentId };
        var finalSaveHeldErrorRow = false;
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(sessionId, agentId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, SessionWriteFence, CancellationToken>((saved, _, _) => finalSaveHeldErrorRow = saved.History.Any(IsErrorRow))
            .ReturnsAsync(SessionSaveOutcome.Persisted);

        var published = new List<SessionLifecycleEventType>();
        var lifecycleEvents = new SessionLifecycleEvents(NullLogger<SessionLifecycleEvents>.Instance);
        lifecycleEvents.SessionChanged += (lifecycleEvent, _) => { published.Add(lifecycleEvent.Type); return Task.CompletedTask; };

        string? fannedOut = "not called";
        var deliverer = new Mock<IOutboundResponseDeliverer>();
        deliverer.Setup(d => d.FanOutAsync(It.IsAny<InboundMessage>(), sessionId, It.IsAny<string?>(), It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, SessionId, string?, ConversationId, CancellationToken>((_, _, content, _, _) => fannedOut = content)
            .Returns(Task.CompletedTask);

        // No channel adapter is registered, so the turn takes the blocking branch.
        await using var host = new GatewayHost(
            supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(),
            Mock.Of<ISessionCompactor>(), new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            sessionLifecycleEvents: lifecycleEvents,
            outboundResponseDeliverer: deliverer.Object);

        await host.DispatchAsync(CreateMessage("hello", sessionId: sessionId.Value, channelType: channelType));

        return new BlockingTurn(session, published, fannedOut, finalSaveHeldErrorRow);
    }
}
