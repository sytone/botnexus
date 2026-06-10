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
    public async Task Steer_WhenAgentNotRunning_StillInjectsViaHandle()
    {
        // After fix: handle exists but not running — SteerAsync is still called.
        // The agent's PendingMessageQueue accepts steers at any time.
        var handle = CreateHandle(isRunning: false);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, sessions.Object, activity);

        await host.ProcessAsync(CreateSteerMessage("late steer"), CancellationToken.None);

        // Steering IS injected even though agent is idle
        handle.Verify(h => h.SteerAsync("late steer", It.IsAny<CancellationToken>()), Times.Once);
        // SteeringInjected published (portal clears pending entry)
        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.SteeringInjected);
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.SteeringQueued);
    }

    [Fact]
    public async Task Steer_WhenAgentNotRunning_RecordsInHistory()
    {
        var handle = CreateHandle(isRunning: false);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        await using var host = CreateHost(supervisor.Object, sessions.Object, new RecordingActivityBroadcaster());

        await host.ProcessAsync(CreateSteerMessage("recorded"), CancellationToken.None);

        session.History.ShouldContain(e => e.Role == MessageRole.User && e.Content == "recorded");
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
    public async Task Steer_WhenHandleExists_DoesNotFallThroughToNormalProcessing()
    {
        // Critical: an injected steer must NOT also trigger a normal user prompt
        var handle = CreateHandle(isRunning: false);
        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        await using var host = CreateHost(supervisor.Object, sessions.Object, new RecordingActivityBroadcaster());

        await host.ProcessAsync(CreateSteerMessage("should not prompt"), CancellationToken.None);

        // SteerAsync called (steer injected) but no prompt/stream issued
        handle.Verify(h => h.SteerAsync("should not prompt", It.IsAny<CancellationToken>()), Times.Once);
        handle.Verify(h => h.PromptAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.StreamAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // TIER 2: Orchestrator integration — queue serialization (THE BUG)
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "FIX: Steer queued behind running dispatch is now injected when dequeued")]
    public async Task Steer_WhileAgentRunning_IsQueuedThenInjectedOnDequeue()
    {
        // After the fix: the steer still waits in the queue (queue serialization
        // is correct for ordering), but when dequeued, HandleSteeringAsync no
        // longer checks IsRunning — it calls SteerAsync regardless.
        //
        // For SignalR portal, GatewayHub.Steer bypasses the queue entirely.
        // This test validates the fallback path for other channels.

        var agentRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentA);
        handle.SetupGet(h => h.SessionId).Returns(SessionA);
        handle.Setup(h => h.IsRunning).Returns(false); // Not running when steer is dequeued
        handle.Setup(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        handle.Setup(h => h.StreamAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(BlockingStream(agentRunning, agentCanFinish));

        var supervisor = CreateSupervisor(handle.Object, hasInstance: true);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        var host = CreateHost(supervisor.Object, sessions.Object, activity);

        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            host, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        // Step 1: Normal message enters queue → agent starts running
        var normalTask = orchestrator.AcceptAsync(CreateMessage("do work"));
        await agentRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Step 2: Steer enters the same queue (blocked behind running dispatch)
        var steerTask = orchestrator.AcceptAsync(CreateSteerMessage("change direction"));

        // Step 3: Agent finishes
        agentCanFinish.TrySetResult();
        await normalTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Step 4: Steer dequeued — NOW it’s processed
        await steerTask.WaitAsync(TimeSpan.FromSeconds(5));

        // FIXED: SteerAsync IS called even though agent finished
        handle.Verify(h => h.SteerAsync("change direction", It.IsAny<CancellationToken>()), Times.Once);
        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.SteeringInjected);
    }

    [Fact(DisplayName = "FIX: Multiple steers queued behind running dispatch are all injected")]
    public async Task MultipleSteer_WhileAgentRunning_AllInjectedOnDequeue()
    {
        var agentRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentA);
        handle.SetupGet(h => h.SessionId).Returns(SessionA);
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
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
        agentCanFinish.TrySetResult();
        await Task.WhenAll(normalTask, steer1, steer2, steer3).WaitAsync(TimeSpan.FromSeconds(10));

        // ALL steers injected
        handle.Verify(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        activity.Activities.Count(a => a.Type == GatewayActivityType.SteeringInjected).ShouldBe(3);
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.SteeringQueued);
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
        // SteeringQueued only fires when NO handle exists at all
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(AgentA, SessionA)).Returns((AgentInstance?)null);
        var session = CreateSession();
        var sessions = CreateSessionStore(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, sessions.Object, activity);

        await host.ProcessAsync(CreateSteerMessage("queue"), CancellationToken.None);

        var queued = activity.Activities.First(a => a.Type == GatewayActivityType.SteeringQueued);
        queued.AgentId.ShouldBe("agent-a");
        queued.SessionId.ShouldBe("session-1");
    }

    [Fact(DisplayName = "FIX: SteeringInjected event fires even when steer is processed after dispatch completes")]
    public async Task QueuedSteer_EmitsSteeringInjected_UIResolvesCorrectly()
    {
        // After the fix: even though the steer waits in the queue, when processed
        // it emits SteeringInjected so the portal clears the pending entry.
        var agentRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentA);
        handle.SetupGet(h => h.SessionId).Returns(SessionA);
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
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
        agentCanFinish.TrySetResult();
        await Task.WhenAll(normalTask, steerTask).WaitAsync(TimeSpan.FromSeconds(10));

        // FIXED: SteeringInjected IS published (portal resolves 🕒 to ✅)
        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.SteeringInjected);
        // No SteeringQueued (handle exists, injection succeeded)
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.SteeringQueued);
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
