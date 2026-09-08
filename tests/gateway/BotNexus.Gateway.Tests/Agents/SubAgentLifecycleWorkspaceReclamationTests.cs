using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Acceptance coverage for issue #3670: sub-agent workspace reclamation must follow the run's own
/// lifecycle, and both reclamation routes must be traceable to one operator query.
/// <para>
/// #3569 shipped the backstop half - a mandatory liveness probe the age-based sweep consults before
/// deleting. This suite pins the lifecycle half. The distinction that matters is <b>timing</b>: a
/// terminated sub-agent's workspace is reclaimed at the terminal transition itself, with no timer,
/// no sweep pass and no elapsed-time threshold anywhere in the path. Every assertion below runs
/// against a manager that has no sweeper wired at all, so a green result cannot be explained by a
/// background sweep happening to fire.
/// </para>
/// </summary>
public sealed class SubAgentLifecycleWorkspaceReclamationTests
{
    private static readonly AgentId ParentAgentId = AgentId.From("parent-agent");
    private static readonly SessionId ParentSessionId = SessionId.From("parent-session");
    private static readonly ConversationId ConvId = ConversationId.From("inherited-conv");

    /// <summary>
    /// AC1, the core lifecycle guarantee. A run that reaches a terminal state has its workspace
    /// reclaimed as part of that transition. No sweeper exists in this graph and no clock is
    /// advanced, so the reclamation can only have come from the lifecycle path.
    /// </summary>
    [Fact]
    public async Task CompletedSubAgent_HasWorkspaceReclaimed_WithoutAnySweep()
    {
        var workspaceManager = CreateWorkspaceManager();
        var manager = CreateManager(CreateSuccessfulHandle(), workspaceManager);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        await WaitUntilRetiredAsync(manager, spawned.SubAgentId);

        workspaceManager.Verify(
            w => w.TryCleanupWorkspace(It.Is<string>(id =>
                id.StartsWith("parent-agent--subagent--", StringComparison.Ordinal))),
            Times.Once);
    }

    /// <summary>
    /// AC3 / the sad path that must never regress. A sub-agent that is still RUNNING keeps its
    /// workspace, no matter how long it runs. This is the shape that destroyed 37 live runs in a
    /// week under the time-only sweep: healthy run, idle workspace, expired timestamp.
    /// </summary>
    [Fact]
    public async Task RunningSubAgent_WorkspaceIsNeverReclaimed()
    {
        // Synchronize on the run actually being INSIDE its prompt call rather than sleeping. The
        // signal makes the observation deterministic: at the moment we assert, the sub-agent is
        // provably mid-run with an idle workspace - the exact state the time-only sweep misread as
        // dead. A wall-clock wait would only make the same assertion probabilistically.
        var enteredRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspaceManager = CreateWorkspaceManager();
        var manager = CreateManager(CreateHangingHandle(enteredRun), workspaceManager);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        await enteredRun.Task.WaitAsync(TimeSpan.FromSeconds(30));

        (await manager.GetAsync(spawned.SubAgentId))!.Status.ShouldBe(SubAgentStatus.Running);
        workspaceManager.Verify(w => w.TryCleanupWorkspace(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// AC1 across dispositions: a killed run is terminal too, so it reclaims on the same path.
    /// A completion-only implementation would leak every workspace belonging to a killed run.
    /// </summary>
    [Fact]
    public async Task KilledSubAgent_HasWorkspaceReclaimed()
    {
        var workspaceManager = CreateWorkspaceManager();
        var manager = CreateManager(CreateHangingHandle(), workspaceManager);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        (await manager.KillAsync(spawned.SubAgentId, ParentSessionId)).ShouldBeTrue();

        workspaceManager.Verify(
            w => w.TryCleanupWorkspace(It.Is<string>(id =>
                id.StartsWith("parent-agent--subagent--", StringComparison.Ordinal))),
            Times.Once);
    }

    /// <summary>
    /// The audit line must name the TERMINAL status that actually caused the reclamation. On the
    /// kill path the teardown runs BEFORE the record's status flips to <c>Killed</c>, so a naive
    /// read of the record at cleanup time reports <c>Running</c> - an audit trail that says a live
    /// run's workspace was reclaimed, which is precisely the false alarm #3569 taught operators to
    /// treat as an emergency. The terminal disposition is therefore passed in explicitly.
    /// </summary>
    [Fact]
    public async Task KilledSubAgent_AuditLine_NamesKilledNotRunning()
    {
        var logger = new CapturingLogger<DefaultSubAgentManager>();
        var workspaceManager = CreateWorkspaceManager();
        var manager = CreateManager(CreateHangingHandle(), workspaceManager, logger);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        (await manager.KillAsync(spawned.SubAgentId, ParentSessionId)).ShouldBeTrue();

        var audit = logger.Entries.FirstOrDefault(entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains(
                SubAgentWorkspaceReclamationAudit.MessagePrefix,
                StringComparison.Ordinal));

        audit.Message.ShouldNotBeNull();
        audit.Message.ShouldContain(nameof(SubAgentStatus.Killed));
        audit.Message.ShouldNotContain(nameof(SubAgentStatus.Running));
    }

    /// <summary>
    /// AC4. The lifecycle route must emit one audit line per removal at Information, carrying the
    /// same <c>Sub-agent workspace reclaimed</c> prefix the sweeper uses, so one operator query
    /// returns both routes. Before this, lifecycle reclamation logged at Debug with unrelated
    /// wording - invisible in production and unjoinable with the sweeper's audit trail.
    /// </summary>
    [Fact]
    public async Task LifecycleReclamation_EmitsAuditLine_MatchingTheSweeperFormat()
    {
        var logger = new CapturingLogger<DefaultSubAgentManager>();
        var workspaceManager = CreateWorkspaceManager();
        var manager = CreateManager(CreateSuccessfulHandle(), workspaceManager, logger);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        await WaitUntilRetiredAsync(manager, spawned.SubAgentId);

        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains(
                SubAgentWorkspaceReclamationAudit.MessagePrefix,
                StringComparison.Ordinal)
            && entry.Message.Contains("parent-agent--subagent--", StringComparison.Ordinal));
    }

    /// <summary>
    /// The audit line describes an actual removal, so it must not be emitted when nothing was
    /// removed. A line logged unconditionally would make the operator query report phantom
    /// reclamations and destroy the trail's usefulness as evidence.
    /// </summary>
    [Fact]
    public async Task LifecycleReclamation_DoesNotEmitAuditLine_WhenNothingWasRemoved()
    {
        var logger = new CapturingLogger<DefaultSubAgentManager>();
        var workspaceManager = CreateWorkspaceManager(cleanupResult: false);
        var manager = CreateManager(CreateSuccessfulHandle(), workspaceManager, logger);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        await WaitUntilRetiredAsync(manager, spawned.SubAgentId);

        logger.Entries.ShouldNotContain(entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains(
                SubAgentWorkspaceReclamationAudit.MessagePrefix,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// Reclamation is best-effort: a workspace manager that throws must not break the terminal
    /// transition. The run must still reach Completed and still retire, because a failed disk
    /// delete is a hygiene problem while a stuck terminal transition strands the parent forever.
    /// </summary>
    [Fact]
    public async Task ReclamationFailure_DoesNotBreakTheTerminalTransition()
    {
        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager
            .Setup(w => w.TryCleanupWorkspace(It.IsAny<string>()))
            .Throws(new IOException("workspace is held by another process"));

        var manager = CreateManager(CreateSuccessfulHandle(), workspaceManager);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        await WaitUntilRetiredAsync(manager, spawned.SubAgentId);

        (await manager.GetAsync(spawned.SubAgentId))!.Status.ShouldBe(SubAgentStatus.Completed);
    }

    /// <summary>
    /// The reclamation runs once per sub-agent, not once per path that can reach cleanup. Both
    /// <c>KillAsync</c> and the completion <c>finally</c> route through the same teardown, so a
    /// missing once-only gate would attempt a second delete against an already-reclaimed directory.
    /// </summary>
    [Fact]
    public async Task Reclamation_HappensAtMostOnce_WhenKillRacesCompletion()
    {
        var workspaceManager = CreateWorkspaceManager();
        var manager = CreateManager(CreateSuccessfulHandle(), workspaceManager);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        await WaitUntilRetiredAsync(manager, spawned.SubAgentId);

        // The run already completed and tore down; a late kill must not reclaim a second time.
        await manager.KillAsync(spawned.SubAgentId, ParentSessionId);

        workspaceManager.Verify(w => w.TryCleanupWorkspace(It.IsAny<string>()), Times.Once);
    }

    private static Task WaitUntilRetiredAsync(DefaultSubAgentManager manager, string subAgentId)
        => TestAwait.EventuallyAsync(
            () => manager.IsRetiredForTest(subAgentId),
            $"sub-agent '{subAgentId}' to reach its terminal cleanup",
            timeout: TimeSpan.FromSeconds(30));

    private static Mock<IAgentWorkspaceManager> CreateWorkspaceManager(bool cleanupResult = true)
    {
        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager
            .Setup(w => w.TryCleanupWorkspace(It.IsAny<string>()))
            .Returns(cleanupResult);
        return workspaceManager;
    }

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = ParentAgentId,
            ParentSessionId = ParentSessionId,
            Task = "Do background work",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConvId
        };

    private static DefaultSubAgentManager CreateManager(
        Mock<IAgentHandle> childHandle,
        Mock<IAgentWorkspaceManager> workspaceManager,
        ILogger<DefaultSubAgentManager>? logger = null)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.IsAny<SessionId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.GetOrCreateAsync(ParentAgentId, ParentSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccessfulHandle().Object);
        supervisor
            .Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(ParentAgentId))
            .Returns(new AgentDescriptor
            {
                AgentId = ParentAgentId,
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "copilot"
            });

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            Mock.Of<IChannelDispatcher>(),
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            logger ?? new CapturingLogger<DefaultSubAgentManager>(),
            workspaceManager: workspaceManager.Object);
    }

    private static Mock<IAgentHandle> CreateSuccessfulHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(ParentAgentId);
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session"));
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "completed" });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        handle.Setup(h => h.FollowUpAsync(It.IsAny<AgentTranscriptMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }

    private static Mock<IAgentHandle> CreateHangingHandle(TaskCompletionSource? enteredRun = null)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(ParentAgentId);
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                // Signal that the run is genuinely in flight before parking forever, so a test can
                // observe the running state instead of guessing at it with a timed wait.
                enteredRun?.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentResponse { Content = "never" };
            });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        handle.Setup(h => h.FollowUpAsync(It.IsAny<AgentTranscriptMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }

    /// <summary>Captures real log records so the audit assertions read emitted text, not intent.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
