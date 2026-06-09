using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Steering;

/// <summary>
/// Comprehensive tests for the gateway steering pipeline.
/// Tests are structured in three tiers:
///
/// 1. ProcessAsync unit tests — steering behaviour given IsRunning state
/// 2. Orchestrator integration tests — queue serialization causes steering discard (THE BUG)
/// 3. Activity/feedback correctness — SteeringQueued vs SteeringInjected events
///
/// These tests PROVE the design flaw: the per-session FIFO queue serializes steer
/// messages behind running dispatches, guaranteeing that HandleSteeringAsync always
/// sees IsRunning=false by the time it executes.
/// </summary>
public sealed class GatewaySteeringTests : IAsyncLifetime
{
    // Shared identifiers
    private static readonly AgentId AgentA = AgentId.From("agent-a");
    private static readonly SessionId SessionA = SessionId.From("session-1");

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    // ─────────────────────────────────────────────────────────────────────────────
    // TIER 1: ProcessAsync unit tests — steering in isolation
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Steer_WhenAgentRunning_CallsSteerAsyncOnHandle()
    {
        // Arrange: handle reports IsRunning=true, instance exists
        var handle = CreateHandle(isRunning: true);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, sessions.Object, activity);

        // Act: dispatch a steer message directly (bypasses orchestrator queue for unit test)
        await host.ProcessAsync(CreateSteerMessage("focus on tests"), CancellationToken.None);

        // Assert: SteerAsync was called
        handle.Verify(h => h.SteerAsync("focus on tests", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Steer_WhenAgentRunning_RecordsEntryInSessionHistory()
    {
        var handle = CreateHandle(isRunning: true);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, sessions.Object, activity);

        await host.ProcessAsync(CreateSteerMessage("change direction"), CancellationToken.None);

        session.History.ShouldContain(e => e.Role == MessageRole.User && e.Content == "change direction");
        sessions.Verify(s => s.SaveAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Steer_WhenAgentRunning_PublishesSteeringInjectedActivity()
    {
        var handle = CreateHandle(isRunning: true);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, sessions.Object, activity);

        await host.ProcessAsync(CreateSteerMessage("go faster"), CancellationToken.None);

        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.SteeringInjected);
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.SteeringQueued);
    }

    [Fact]
    public async Task Steer_WhenAgentRunning_DoesNotCallPromptAsync()
    {
        var handle = CreateHandle(isRunning: true);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        await using var host = CreateHost(supervisor.Object, sessions.Object, new RecordingActivityBroadcaster());

        await host.ProcessAsync(CreateSteerMessage("steer content"), CancellationToken.None);

        handle.Verify(h => h.PromptAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.StreamAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Steer_WhenAgentNotRunning_DiscardsAndPublishesSteeringQueued()
    {
        // Agent has an instance but is NOT running
        var handle = CreateHandle(isRunning: false);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, sessions.Object, activity);

        await host.ProcessAsync(CreateSteerMessage("too late"), CancellationToken.None);

        // Steering is discarded
        handle.Verify(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // SteeringQueued published (portal shows "🕒")
        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.SteeringQueued);
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.SteeringInjected);
    }

    [Fact]
    public async Task Steer_WhenAgentNotRunning_DoesNotRecordInHistory()
    {
        var handle = CreateHandle(isRunning: false);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        await using var host = CreateHost(supervisor.Object, sessions.Object, new RecordingActivityBroadcaster());

        await host.ProcessAsync(CreateSteerMessage("discarded"), CancellationToken.None);

        session.History.ShouldNotContain(e => e.Content == "discarded");
    }

    [Fact]
    public async Task Steer_WhenNoInstanceExists_DiscardsAndPublishesSteeringQueued()
    {
        // No instance at all — agent was never started for this session
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(AgentA, SessionA)).Returns((AgentInstance?)null);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, sessions.Object, activity);

        await host.ProcessAsync(CreateSteerMessage("no agent"), CancellationToken.None);

        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.SteeringQueued);
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.SteeringInjected);
    }

    [Fact]
    public async Task Steer_WhenAgentNotRunning_DoesNotFallThroughToNormalProcessing()
    {
        // Critical: discarded steer must NOT become a normal user prompt
        var handle = CreateHandle(isRunning: false);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        await using var host = CreateHost(supervisor.Object, sessions.Object, new RecordingActivityBroadcaster());

        await host.ProcessAsync(CreateSteerMessage("should not prompt"), CancellationToken.None);

        handle.Verify(h => h.PromptAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.StreamAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // TIER 2: Orchestrator integration — queue serialization (THE BUG)
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "BUG: Steer sent while agent is running is ALWAYS discarded due to queue serialization")]
    public async Task Steer_WhileAgentRunning_IsBlockedByQueueAndDiscarded()
    {
        // This test PROVES the design flaw:
        // 1. A normal message enters the queue → agent starts running
        // 2. While agent is running, a steer message enters the SAME queue
        // 3. The queue is SingleReader — steer waits behind the running dispatch
        // 4. When the normal message finishes, agent stops running (IsRunning=false)
        // 5. Steer is dequeued → HandleSteeringAsync → IsRunning=false → DISCARDED
        //
        // Expected: steer should be injected mid-turn
        // Actual: steer is always too late

        var agentRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isRunning = true;

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentA);
        handle.SetupGet(h => h.SessionId).Returns(SessionA);
        handle.Setup(h => h.IsRunning).Returns(() => isRunning);
        handle.Setup(h => h.StreamAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(BlockingStream(agentRunning, agentCanFinish));

        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        var host = CreateHost(supervisor.Object, sessions.Object, activity);

        // Wire up the orchestrator — messages go through the per-session queue
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            host, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        // Step 1: Normal message enters queue — agent starts running
        var normalTask = orchestrator.AcceptAsync(CreateMessage("do work"));
        await agentRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Step 2: While agent IS running, send a steer message on the same queue
        var steerTask = orchestrator.AcceptAsync(CreateSteerMessage("change direction"));

        // Step 3: The steer is stuck in the queue. Verify it hasn't been processed yet.
        await Task.Delay(200);
        handle.Verify(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "Steer should still be in queue while agent is running");

        // Step 4: Agent finishes — IsRunning transitions to false
        isRunning = false;
        agentCanFinish.TrySetResult();
        await normalTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Step 5: NOW the steer is dequeued, but agent is no longer running
        await steerTask.WaitAsync(TimeSpan.FromSeconds(5));

        // ASSERT THE BUG: SteerAsync was NEVER called
        handle.Verify(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "BUG PROVEN: Steer was discarded because queue serialization guarantees IsRunning=false by dequeue time");

        // SteeringQueued was published (UI shows 🕒 forever)
        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.SteeringQueued,
            "Portal stuck showing '🕒 Steer queued' with no resolution");
        // SteeringInjected was NEVER published (UI never clears)
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.SteeringInjected,
            "No SteeringInjected means portal UI never resolves");
    }

    [Fact(DisplayName = "BUG: Multiple steers while agent running are ALL discarded")]
    public async Task MultipleSteer_WhileAgentRunning_AllDiscarded()
    {
        var agentRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isRunning = true;

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentA);
        handle.SetupGet(h => h.SessionId).Returns(SessionA);
        handle.Setup(h => h.IsRunning).Returns(() => isRunning);
        handle.Setup(h => h.StreamAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(BlockingStream(agentRunning, agentCanFinish));

        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        var host = CreateHost(supervisor.Object, sessions.Object, activity);
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            host, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        // Agent running
        var normalTask = orchestrator.AcceptAsync(CreateMessage("work"));
        await agentRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Send 3 steers while agent is busy
        var steer1 = orchestrator.AcceptAsync(CreateSteerMessage("steer-1"));
        var steer2 = orchestrator.AcceptAsync(CreateSteerMessage("steer-2"));
        var steer3 = orchestrator.AcceptAsync(CreateSteerMessage("steer-3"));

        // Agent finishes
        isRunning = false;
        agentCanFinish.TrySetResult();
        await Task.WhenAll(normalTask, steer1, steer2, steer3).WaitAsync(TimeSpan.FromSeconds(10));

        // ALL steers discarded
        handle.Verify(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        activity.Activities.Count(a => a.Type == GatewayActivityType.SteeringQueued).ShouldBe(3);
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.SteeringInjected);
    }

    [Fact]
    public async Task Steer_OnDifferentSession_IsProcessedIndependently()
    {
        // A steer on a DIFFERENT session should NOT be blocked by the running agent
        var agentRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handleA = new Mock<IAgentHandle>();
        handleA.SetupGet(h => h.AgentId).Returns(AgentA);
        handleA.SetupGet(h => h.SessionId).Returns(SessionA);
        handleA.Setup(h => h.IsRunning).Returns(true);
        handleA.Setup(h => h.StreamAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(BlockingStream(agentRunning, agentCanFinish));

        var sessionB = SessionId.From("session-2");
        var handleB = new Mock<IAgentHandle>();
        handleB.SetupGet(h => h.AgentId).Returns(AgentA);
        handleB.SetupGet(h => h.SessionId).Returns(sessionB);
        handleB.Setup(h => h.IsRunning).Returns(true);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(AgentA, SessionA)).Returns(CreateInstance(SessionA));
        supervisor.Setup(s => s.GetInstance(AgentA, sessionB)).Returns(CreateInstance(sessionB));
        supervisor.Setup(s => s.GetOrCreateAsync(AgentA, SessionA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handleA.Object);
        supervisor.Setup(s => s.GetOrCreateAsync(AgentA, sessionB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handleB.Object);

        var sessionStore = new Mock<ISessionStore>();
        var gwSessionA = new GatewaySession { SessionId = SessionA, AgentId = AgentA };
        var gwSessionB = new GatewaySession { SessionId = sessionB, AgentId = AgentA };
        sessionStore.Setup(s => s.GetAsync(SessionA, It.IsAny<CancellationToken>())).ReturnsAsync(gwSessionA);
        sessionStore.Setup(s => s.GetAsync(sessionB, It.IsAny<CancellationToken>())).ReturnsAsync(gwSessionB);
        sessionStore.Setup(s => s.GetOrCreateAsync(SessionA, AgentA, It.IsAny<CancellationToken>())).ReturnsAsync(gwSessionA);
        sessionStore.Setup(s => s.GetOrCreateAsync(sessionB, AgentA, It.IsAny<CancellationToken>())).ReturnsAsync(gwSessionB);
        sessionStore.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var activity = new RecordingActivityBroadcaster();
        var host = CreateHost(supervisor.Object, sessionStore.Object, activity);
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            host, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        // Session A: agent running
        var normalTask = orchestrator.AcceptAsync(CreateMessage("work", sessionId: "session-1"));
        await agentRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Session B: steer on a different session — should NOT be blocked
        var steerMsg = CreateSteerMessage("steer for B", sessionId: "session-2");
        var steerTask = orchestrator.AcceptAsync(steerMsg);
        await steerTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Steer on B succeeded because it's a different queue key
        handleB.Verify(h => h.SteerAsync("steer for B", It.IsAny<CancellationToken>()), Times.Once);

        // Cleanup
        agentCanFinish.TrySetResult();
        await normalTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Steer_WhenAgentNotRunning_DoesNotConvertToUserPrompt()
    {
        // Critical: even through the orchestrator, a discarded steer must NOT become a prompt
        var handle = CreateHandle(isRunning: false);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var host = CreateHost(supervisor.Object, sessions.Object, new RecordingActivityBroadcaster());
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            host, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        await orchestrator.AcceptAsync(CreateSteerMessage("should not prompt"));

        handle.Verify(h => h.PromptAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.StreamAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // TIER 3: Activity/feedback event correctness
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SteeringInjected_IncludesAgentIdAndSessionId()
    {
        var handle = CreateHandle(isRunning: true);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, sessions.Object, activity);

        await host.ProcessAsync(CreateSteerMessage("inject"), CancellationToken.None);

        var injected = activity.Activities.First(a => a.Type == GatewayActivityType.SteeringInjected);
        injected.AgentId.ShouldBe("agent-a");
        injected.SessionId.ShouldBe("session-1");
    }

    [Fact]
    public async Task SteeringQueued_IncludesAgentIdAndSessionId()
    {
        var handle = CreateHandle(isRunning: false);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, sessions.Object, activity);

        await host.ProcessAsync(CreateSteerMessage("queue"), CancellationToken.None);

        var queued = activity.Activities.First(a => a.Type == GatewayActivityType.SteeringQueued);
        queued.AgentId.ShouldBe("agent-a");
        queued.SessionId.ShouldBe("session-1");
    }

    [Fact(DisplayName = "BUG: No SteeringInjected event ever fires when steer is queued behind running dispatch")]
    public async Task QueuedSteer_NeverEmitsSteeringInjected_LeavingUIStuck()
    {
        // This test proves the portal UI bug: 🕒 Steer stuck forever
        // The portal adds to PendingSteeringQueue on SteeringQueued event,
        // but never receives SteeringInjected to clear it.
        var agentRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isRunning = true;

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentA);
        handle.SetupGet(h => h.SessionId).Returns(SessionA);
        handle.Setup(h => h.IsRunning).Returns(() => isRunning);
        handle.Setup(h => h.StreamAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(BlockingStream(agentRunning, agentCanFinish));

        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        var host = CreateHost(supervisor.Object, sessions.Object, activity);
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            host, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        // Start agent
        var normalTask = orchestrator.AcceptAsync(CreateMessage("work"));
        await agentRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Send steer while busy
        var steerTask = orchestrator.AcceptAsync(CreateSteerMessage("redirect"));

        // Agent finishes
        isRunning = false;
        agentCanFinish.TrySetResult();
        await Task.WhenAll(normalTask, steerTask).WaitAsync(TimeSpan.FromSeconds(10));

        // PROVE THE UI BUG:
        // SteeringQueued was published (portal adds to pending queue, shows 🕒)
        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.SteeringQueued);
        // SteeringInjected was NEVER published (portal never clears the pending entry)
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.SteeringInjected);
        // This means the "🕒 Steer redirect..." stays in the UI PERMANENTLY
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // TIER 4: Edge cases
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Steer_WithSupervisorGetOrCreateException_TreatedAsNotRunning()
    {
        // GetInstance returns something, but GetOrCreateAsync throws
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(AgentA, SessionA)).Returns(CreateInstance(SessionA));
        supervisor.Setup(s => s.GetOrCreateAsync(AgentA, SessionA, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("descriptor not found"));

        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, sessions.Object, activity);

        await host.ProcessAsync(CreateSteerMessage("error case"), CancellationToken.None);

        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.SteeringQueued);
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.SteeringInjected);
    }

    [Fact]
    public async Task Steer_WhenSessionExpired_DoesNotProcess()
    {
        // An expired session should reject all messages including steers
        var handle = CreateHandle(isRunning: true);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = new GatewaySession
        {
            SessionId = SessionA,
            AgentId = AgentA,
            Status = SessionStatus.Expired
        };
        var sessions = CreateSessionStore(session);
        await using var host = CreateHost(supervisor.Object, sessions.Object, new RecordingActivityBroadcaster());

        // Expired sessions get reactivated by ProcessAsync before reaching control check.
        // This test validates the session IS reactivated, then steer proceeds.
        await host.ProcessAsync(CreateSteerMessage("steer expired session"), CancellationToken.None);

        // Session was reactivated, so steer should work (agent IS running)
        handle.Verify(h => h.SteerAsync("steer expired session", It.IsAny<CancellationToken>()), Times.Once);
        session.Status.ShouldBe(SessionStatus.Active);
    }

    [Fact]
    public async Task Steer_NormalMessageFollowingDiscardedSteer_IsProcessedNormally()
    {
        // After a steer is discarded, the next normal message must still work
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentA);
        handle.SetupGet(h => h.SessionId).Returns(SessionA);
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.StreamAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "response" }
            ]));

        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var channel = CreateChannel();
        await using var host = CreateHost(supervisor.Object, sessions.Object, new RecordingActivityBroadcaster(), channel.Object);
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            host, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        // First: steer (discarded)
        await orchestrator.AcceptAsync(CreateSteerMessage("discarded"));
        // Second: normal message (should work)
        await orchestrator.AcceptAsync(CreateMessage("follow up"));

        handle.Verify(h => h.StreamAsync(
            It.Is<UserMessage>(m => m.Content == "follow up"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static Mock<IAgentHandle> CreateHandle(bool isRunning)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentA);
        handle.SetupGet(h => h.SessionId).Returns(SessionA);
        handle.Setup(h => h.IsRunning).Returns(isRunning);
        handle.Setup(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return handle;
    }

    private static Mock<IAgentSupervisor> CreateSupervisor(IAgentHandle handle, bool hasInstance)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        if (hasInstance)
        {
            supervisor.Setup(s => s.GetInstance(AgentA, SessionA)).Returns(CreateInstance(SessionA));
        }
        else
        {
            supervisor.Setup(s => s.GetInstance(AgentA, SessionA)).Returns((AgentInstance?)null);
        }
        supervisor.Setup(s => s.GetOrCreateAsync(AgentA, SessionA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);
        return supervisor;
    }

    private static AgentInstance CreateInstance(SessionId sessionId) => new()
    {
        InstanceId = $"agent-a::{sessionId.Value}",
        AgentId = AgentA,
        SessionId = sessionId,
        IsolationStrategy = "in-process"
    };

    private static GatewaySession CreateSession() => new()
    {
        SessionId = SessionA,
        AgentId = AgentA
    };

    private static Mock<ISessionStore> CreateSessionStore(GatewaySession session)
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(SessionA, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        store.Setup(s => s.GetOrCreateAsync(SessionA, AgentA, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        store.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return store;
    }

    private static Mock<IChannelAdapter> CreateChannel()
    {
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns("web");
        channel.SetupGet(c => c.DisplayName).Returns("web");
        channel.SetupGet(c => c.SupportsStreaming).Returns(true);
        channel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        channel.Setup(c => c.SendStreamDeltaAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return channel;
    }

    private static GatewayHost CreateHost(
        IAgentSupervisor supervisor,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IChannelAdapter? channel = null)
    {
        // Default to a streaming channel so ProcessAsync takes the StreamAsync path
        var effectiveChannel = channel ?? CreateStreamingChannel();

        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([effectiveChannel]);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns(effectiveChannel);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns(effectiveChannel);

        return new GatewayHost(
            supervisor,
            router.Object,
            sessions,
            activity,
            channelManager.Object,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            sessionQueueCapacity: 64);
    }

    private static IChannelAdapter CreateStreamingChannel()
    {
        var ch = new Mock<IChannelAdapter>();
        ch.SetupGet(c => c.ChannelType).Returns("web");
        ch.SetupGet(c => c.DisplayName).Returns("web");
        ch.SetupGet(c => c.SupportsStreaming).Returns(true);
        ch.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        ch.Setup(c => c.SendStreamDeltaAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return ch.Object;
    }

    private static InboundMessage CreateMessage(string content, string sessionId = "session-1")
        => new()
        {
            ChannelType = "web",
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            ChannelAddress = ChannelAddress.From("conv-1"),
            Content = content,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, sessionId, null),
            Metadata = new Dictionary<string, object?>()
        };

    private static InboundMessage CreateSteerMessage(string content, string sessionId = "session-1")
        => new()
        {
            ChannelType = "web",
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            ChannelAddress = ChannelAddress.From("conv-1"),
            Content = content,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, sessionId, null),
            Metadata = new Dictionary<string, object?> { ["control"] = "steer" }
        };

    private static IAsyncEnumerable<AgentStreamEvent> BlockingStream(
        TaskCompletionSource agentRunning,
        TaskCompletionSource agentCanFinish)
    {
        return Impl();

        async IAsyncEnumerable<AgentStreamEvent> Impl()
        {
            agentRunning.TrySetResult();
            await agentCanFinish.Task;
            yield return new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "done" };
        }
    }

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        public List<GatewayActivity> Activities { get; } = [];

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
        {
            Activities.Add(activity);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
                await Task.Delay(10, cancellationToken);
            yield break;
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
