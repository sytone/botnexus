using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Triggers;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Api;

public sealed class HeartbeatTriggerTests
{
    [Fact]
    public void Type_IsHeartbeat()
    {
        var trigger = BuildTrigger();
        trigger.Type.ShouldBe(TriggerType.Heartbeat);
    }

    [Fact]
    public void DisplayName_IsHeartbeat()
    {
        var trigger = BuildTrigger();
        trigger.DisplayName.ShouldBe("Heartbeat");
    }

    [Fact]
    public async Task CreateSessionAsync_NonSoulAgent_CreatesHeartbeatSession()
    {
        var agentId = AgentId.From("agent-a");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: false);
        var captured = SetupPromptCapture(mocks, agentId);

        var sessionId = await trigger.CreateSessionAsync(agentId, "Heartbeat ping");

        sessionId.Value.ShouldStartWith("heartbeat:");
        captured.Prompt.ShouldBe("Heartbeat ping");
        mocks.Sessions.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CreateSessionAsync_SoulAgent_WithActiveSoulSession_UsesIt()
    {
        var agentId = AgentId.From("agent-a");
        var soulSessionId = SessionId.From("agent-a::soul::2026-05-18");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: true);

        var existingSoulSession = new GatewaySession { SessionId = soulSessionId, AgentId = agentId, Status = BotNexus.Gateway.Abstractions.Models.SessionStatus.Active, UpdatedAt = DateTimeOffset.UtcNow };
        // P9-E (#645): heartbeat discovers today's soul session via Metadata["soulDate"]
        // rather than the deleted SessionType.Soul discriminator. The shape is AgentSelf.
        existingSoulSession.SessionType = SessionType.AgentSelf;
        existingSoulSession.Metadata["soulDate"] = "2026-05-18";

        mocks.Sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([existingSoulSession]);
        mocks.Sessions.Setup(s => s.GetOrCreateAsync(soulSessionId, agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingSoulSession);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "HEARTBEAT_OK" });
        mocks.Supervisor.Setup(s => s.GetOrCreateAsync(agentId, soulSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessionId = await trigger.CreateSessionAsync(agentId, "Heartbeat ping");

        sessionId.ShouldBe(soulSessionId);
    }

    [Fact]
    public async Task CreateSessionAsync_SoulAgent_NoActiveSoulSession_FallsBackToHeartbeatSession()
    {
        var agentId = AgentId.From("agent-a");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: true);

        // No active soul session
        mocks.Sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        SetupPromptCapture(mocks, agentId);

        var sessionId = await trigger.CreateSessionAsync(agentId, "Heartbeat ping");

        // Should fall back and create a heartbeat: prefixed session
        sessionId.Value.ShouldStartWith("heartbeat:");
    }

    [Fact]
    public async Task CreateSessionAsync_RecordsUserAndAssistantEntries()
    {
        var agentId = AgentId.From("agent-a");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: false);
        var captured = SetupPromptCapture(mocks, agentId);

        await trigger.CreateSessionAsync(agentId, "Heartbeat ping");

        // Session should have been saved at least twice (before and after prompt)
        mocks.Sessions.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        captured.Response.ShouldBe("HEARTBEAT_OK");
    }

    [Fact]
    public async Task CreateSessionAsync_PropagatesCronJobIdAndModel()
    {
        var agentId = AgentId.From("agent-a");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: false);
        GatewaySession? savedSession = null;

        SetupPromptCapture(mocks, agentId);
        mocks.Sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((session, _) => savedSession = session)
            .Returns(Task.CompletedTask);

        await trigger.CreateSessionAsync(agentId, "Heartbeat ping",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("heartbeat:agent-a"),
                ModelOverride = "openai/gpt-4.1"
            });

        savedSession.ShouldNotBeNull();
        savedSession!.Metadata.ShouldContainKey("cronJobId");
        savedSession.Metadata["cronJobId"].ShouldBe("heartbeat:agent-a");
        savedSession.Metadata.ShouldContainKey("modelOverride");
        savedSession.Metadata["modelOverride"].ShouldBe("openai/gpt-4.1");
    }

    // -----------------------------------------------------------------
    // Phase 2 race-safety regression suite (#573)
    //
    // The shared `ExecuteInSessionAsync` runs both the soul path (which
    // reuses today's active soul session — concurrent activity is real)
    // and the heartbeat path (which mints a unique per-run sessionId, so
    // no concurrent activity is possible). All three tests below drive
    // the soul path to exercise the race window.
    //
    // Before #573 the trigger snapshotted history, appended the heartbeat
    // user, awaited the (slow) LLM call, then on ack unconditionally
    // called `session.ReplaceHistory(preSnapshot)` followed by
    // `session.UpdatedAt = preTime`. Both writes were race-unsafe:
    //  - HIGH-1: any AddEntry between snapshot and ReplaceHistory was
    //    silently dropped by the apply.
    //  - HIGH-2: any AddEntry between TryReplace and the UpdatedAt
    //    assignment was overwritten with the stale pre-heartbeat time.
    //
    // Post-fix the trigger uses `AddEntryAndSnapshot` (atomic) and
    // `TryReplaceHistoryFromSnapshot(..., restoreUpdatedAtOnApplied: ...)`
    // (UpdatedAt restoration inside the runtime lock on Applied only).
    // -----------------------------------------------------------------

    [Fact]
    public async Task ExecuteInSessionAsync_AckPath_NoConcurrentActivity_PrunesHeartbeatAndRestoresUpdatedAt()
    {
        var agentId = AgentId.From("agent-a");
        var sessionId = SessionId.From("agent-a::soul::2026-05-31");
        var preHeartbeatTime = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

        var (trigger, mocks) = BuildTriggerWithMocks(soul: true);
        var soulSession = new GatewaySession
        {
            SessionId = sessionId,
            AgentId = agentId,
            Status = GatewaySessionStatus.Active,
            UpdatedAt = preHeartbeatTime,
            // P9-E (#645): soul sessions carry SessionType.AgentSelf + Metadata["soulDate"].
            SessionType = SessionType.AgentSelf,
            Metadata = new Dictionary<string, object?> { ["soulDate"] = "2026-05-31" }
        };
        soulSession.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "u1" });
        soulSession.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "a1" });
        // AddEntry bumps UpdatedAt — re-anchor so the test can prove the
        // restoration happened (vs simply not changing it).
        soulSession.UpdatedAt = preHeartbeatTime;

        mocks.Sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([soulSession]);
        mocks.Sessions.Setup(s => s.GetOrCreateAsync(sessionId, agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(soulSession);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "HEARTBEAT_OK" });
        mocks.Supervisor.Setup(s => s.GetOrCreateAsync(agentId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        await trigger.CreateSessionAsync(agentId, "ping");

        var entries = soulSession.GetHistorySnapshot();
        entries.Select(e => e.Content).ToList().ShouldBe(["u1", "a1"]);
        soulSession.UpdatedAt.ShouldBe(preHeartbeatTime);
    }

    [Fact]
    public async Task ExecuteInSessionAsync_AckPath_ConcurrentAddEntry_PrunesHeartbeatButPreservesConcurrentTail()
    {
        var agentId = AgentId.From("agent-a");
        var sessionId = SessionId.From("agent-a::soul::2026-05-31");
        var preHeartbeatTime = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

        var (trigger, mocks) = BuildTriggerWithMocks(soul: true);
        var soulSession = new GatewaySession
        {
            SessionId = sessionId,
            AgentId = agentId,
            Status = GatewaySessionStatus.Active,
            UpdatedAt = preHeartbeatTime,
            SessionType = SessionType.AgentSelf,
            Metadata = new Dictionary<string, object?> { ["soulDate"] = "2026-05-31" }
        };
        soulSession.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "u1" });
        soulSession.UpdatedAt = preHeartbeatTime;

        mocks.Sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([soulSession]);
        mocks.Sessions.Setup(s => s.GetOrCreateAsync(sessionId, agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(soulSession);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                entered.TrySetResult();
                var content = await release.Task.WaitAsync(ct).ConfigureAwait(false);
                return new AgentResponse { Content = content };
            });
        mocks.Supervisor.Setup(s => s.GetOrCreateAsync(agentId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var triggerTask = trigger.CreateSessionAsync(agentId, "ping");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Concurrent activity on the shared soul session during the LLM window.
        soulSession.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "raced-in" });

        release.TrySetResult("HEARTBEAT_OK");
        await triggerTask;

        var entries = soulSession.GetHistorySnapshot();
        entries.Select(e => e.Content).ToList().ShouldBe(["u1", "raced-in"]);
        // Concurrent activity is real — UpdatedAt must NOT be restored to the
        // pre-heartbeat anchor (would lie about activity timing). The Rebased
        // path stamps UtcNow, which is the legitimate concurrent activity time
        // — explicitly assert NOT the stale restore anchor.
        soulSession.UpdatedAt.ShouldNotBe(preHeartbeatTime);
    }

    [Fact]
    public async Task ExecuteInSessionAsync_AckPath_ConcurrentDestructiveMutation_AbortsAndDoesNotClobberCompactor()
    {
        var agentId = AgentId.From("agent-a");
        var sessionId = SessionId.From("agent-a::soul::2026-05-31");

        var (trigger, mocks) = BuildTriggerWithMocks(soul: true);
        var soulSession = new GatewaySession
        {
            SessionId = sessionId,
            AgentId = agentId,
            Status = GatewaySessionStatus.Active,
            UpdatedAt = DateTimeOffset.UtcNow,
            SessionType = SessionType.AgentSelf,
            Metadata = new Dictionary<string, object?> { ["soulDate"] = "2026-05-31" }
        };
        soulSession.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "u1" });
        soulSession.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "a1" });
        soulSession.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "u2" });

        mocks.Sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([soulSession]);
        mocks.Sessions.Setup(s => s.GetOrCreateAsync(sessionId, agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(soulSession);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                entered.TrySetResult();
                var content = await release.Task.WaitAsync(ct).ConfigureAwait(false);
                return new AgentResponse { Content = content };
            });
        mocks.Supervisor.Setup(s => s.GetOrCreateAsync(agentId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var triggerTask = trigger.CreateSessionAsync(agentId, "ping");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Concurrent destructive mutation — simulates a compactor applying a
        // summary while the heartbeat LLM call is in flight. The compactor's
        // output below intentionally does NOT preserve the heartbeat user (a
        // real compactor would not know it was a heartbeat).
        soulSession.ReplaceHistory([
            new SessionEntry { Role = MessageRole.System, Content = "compaction-summary", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "u2" },
        ]);

        release.TrySetResult("HEARTBEAT_OK");
        await triggerTask;

        // Aborted: the heartbeat prune did NOT run, so the compactor's output
        // survives untouched. The pre-fix code would have called
        // `ReplaceHistory(preSnapshot)` and clobbered the compactor's work to
        // `[u1, a1, u2]`.
        var entries = soulSession.GetHistorySnapshot();
        entries.Select(e => e.Content).ToList().ShouldBe(["compaction-summary", "u2"]);
    }

    // -----------------------------------------------------------------
    // #2127: tool-audit records must survive an ack-shaped heartbeat turn
    // -----------------------------------------------------------------

    [Fact]
    public async Task CreateSessionAsync_AckWithToolActivity_PersistsToolRows_AndDoesNotPrune()
    {
        var agentId = AgentId.From("agent-a");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: false);

        GatewaySession? saved = null;
        mocks.Sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((session, _) => saved = session)
            .Returns(Task.CompletedTask);

        var handle = new Mock<IAgentHandle>();
        // Final text looks like an ack, but a side-effecting tool ran this turn.
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse
            {
                Content = "HEARTBEAT_OK",
                ToolCalls = [new AgentToolCallInfo("call-1", "memory_save", false, "{\"note\":\"x\"}", "saved")]
            });
        mocks.Supervisor.Setup(s => s.GetOrCreateAsync(agentId, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        await trigger.CreateSessionAsync(agentId, "Heartbeat ping");

        saved.ShouldNotBeNull();
        var history = saved!.GetHistorySnapshot();
        // The heartbeat user turn must NOT be pruned when tools ran.
        history.ShouldContain(e => e.Role == MessageRole.User && e.Trigger == TriggerType.Heartbeat);
        // The tool row must be durably recorded with full metadata.
        var toolRow = history.Where(e => e.Role == MessageRole.Tool).ShouldHaveSingleItem();
        toolRow.ToolCallId.ShouldBe("call-1");
        toolRow.ToolName.ShouldBe("memory_save");
        toolRow.ToolArgs.ShouldNotBeNull().ShouldContain("note");
        toolRow.Content.ShouldBe("saved");
        // The assistant text is retained too.
        history.ShouldContain(e => e.Role == MessageRole.Assistant && e.Content == "HEARTBEAT_OK");
    }

    [Fact]
    public async Task CreateSessionAsync_AckWithoutToolActivity_StillPrunesTurn()
    {
        var agentId = AgentId.From("agent-a");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: false);

        GatewaySession? saved = null;
        mocks.Sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((session, _) => saved = session)
            .Returns(Task.CompletedTask);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "HEARTBEAT_OK" });
        mocks.Supervisor.Setup(s => s.GetOrCreateAsync(agentId, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        await trigger.CreateSessionAsync(agentId, "Heartbeat ping");

        saved.ShouldNotBeNull();
        // Pure ack, no tools: the heartbeat turn is pruned as before.
        saved!.GetHistorySnapshot().ShouldNotContain(e => e.Role == MessageRole.Tool);
        saved.GetHistorySnapshot().ShouldNotContain(e => e.Role == MessageRole.User && e.Trigger == TriggerType.Heartbeat);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private sealed record TriggerMocks(
        Mock<IAgentSupervisor> Supervisor,
        Mock<IAgentRegistry> Registry,
        Mock<IConversationStore> Conversations,
        Mock<ISessionStore> Sessions);

    private static HeartbeatTrigger BuildTrigger()
        => BuildTriggerWithMocks(soul: false).Trigger;

    private static (HeartbeatTrigger Trigger, TriggerMocks Mocks) BuildTriggerWithMocks(bool soul)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        var registry = new Mock<IAgentRegistry>();
        var conversations = new Mock<IConversationStore>();
        var sessions = new Mock<ISessionStore>();

        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test",
            ApiProvider = "test",
            Soul = soul ? new SoulAgentConfig { Enabled = true } : null
        };
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(descriptor);

        // Default: no existing sessions
        sessions.Setup(s => s.ListAsync(It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Default: GetOrCreate returns a new blank session
        sessions.Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId sid, AgentId aid, CancellationToken _) => new GatewaySession { SessionId = sid, AgentId = aid });
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default: no existing conversation
        conversations.Setup(c => c.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation?)null);
        conversations.Setup(c => c.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation conv, CancellationToken _) => conv);
        conversations.Setup(c => c.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mocks = new TriggerMocks(supervisor, registry, conversations, sessions);
        var trigger = new HeartbeatTrigger(
            supervisor.Object,
            registry.Object,
            conversations.Object,
            sessions.Object,
            NullLogger<HeartbeatTrigger>.Instance);

        return (trigger, mocks);
    }

    private sealed class PromptCapture
    {
        public string Prompt { get; set; } = string.Empty;
        public string Response { get; set; } = string.Empty;
    }

    private static PromptCapture SetupPromptCapture(TriggerMocks mocks, AgentId agentId)
    {
        var captured = new PromptCapture();
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((prompt, _) => captured.Prompt = prompt)
            .ReturnsAsync(() =>
            {
                captured.Response = "HEARTBEAT_OK";
                return new AgentResponse { Content = "HEARTBEAT_OK" };
            });

        mocks.Supervisor.Setup(s => s.GetOrCreateAsync(agentId, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        return captured;
    }
}
