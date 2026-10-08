using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// #4537: retirement is the end of owned cleanup, not entry into its once-only gate.
/// Stop readiness and release signals make the incomplete-cleanup observation deterministic.
/// </summary>
public sealed class SubAgentCleanupCompletionBoundaryTests
{
    private static readonly AgentId ParentAgentId = AgentId.From("parent-agent");
    private static readonly SessionId ParentSessionId = SessionId.From("parent-session");
    private static readonly TimeSpan DiagnosticDeadline = TimeSpan.FromSeconds(30);

    /// <summary>Every run-loop terminal route must finish teardown before publishing retirement.</summary>
    [Theory]
    [InlineData(SubAgentStatus.Completed)]
    [InlineData(SubAgentStatus.Failed)]
    [InlineData(SubAgentStatus.TimedOut)]
    public async Task RunSubAgentAsync_StopBlocked_IsNotRetiredUntilWorkspaceCleanupFinishes(
        SubAgentStatus terminalStatus)
    {
        var harness = new CleanupHarness();
        var spawned = await harness.SpawnAsync();
        var runCompletion = harness.Manager.WaitForRunCompletionForTestAsync(spawned.SubAgentId);
        try
        {
            await harness.PromptEntered.Task.WaitAsync(DiagnosticDeadline);
            if (terminalStatus == SubAgentStatus.TimedOut)
                harness.Clock.FireAbsoluteDeadline();
            harness.PromptResult.SetResult(new AgentResponse
            {
                Content = terminalStatus == SubAgentStatus.Failed ? string.Empty : "completed"
            });
            await harness.StopEntered.Task.WaitAsync(DiagnosticDeadline);

            var info = await harness.Manager.GetAsync(spawned.SubAgentId);
            info.ShouldNotBeNull();
            info.Status.ShouldBe(terminalStatus);
            runCompletion.IsCompleted.ShouldBeFalse();
            harness.VerifyStopAndWorkspace(spawned, workspaceCalls: Times.Never());
            harness.VerifyDispatch(Times.Once());
            harness.Manager.IsRetiredForTest(spawned.SubAgentId).ShouldBeFalse(
                "RetiredAt promises finished cleanup; StopAsync is still blocked and workspace cleanup has not run");

            harness.StopRelease.SetResult();
            await runCompletion.WaitAsync(DiagnosticDeadline);

            harness.Manager.IsRetiredForTest(spawned.SubAgentId).ShouldBeTrue();
            harness.RetiredAtWorkspaceCleanup.ShouldBe(false,
                "retirement must not become visible before workspace cleanup returns");
            harness.VerifyStopAndWorkspace(spawned, workspaceCalls: Times.Once());
            harness.VerifyDispatch(Times.Once());
        }
        finally
        {
            harness.ReleaseAll();
            await runCompletion.WaitAsync(DiagnosticDeadline);
        }
    }

    /// <summary>The completed winner stays authoritative while an explicit kill contends during cleanup.</summary>
    [Fact]
    public async Task KillAsync_CompletionWinsWhileStopBlocked_PreservesWinnerAndCleansExactlyOnce()
    {
        var harness = new CleanupHarness();
        var spawned = await harness.SpawnAsync();
        var runCompletion = harness.Manager.WaitForRunCompletionForTestAsync(spawned.SubAgentId);
        try
        {
            await harness.PromptEntered.Task.WaitAsync(DiagnosticDeadline);
            harness.PromptResult.SetResult(new AgentResponse { Content = "completion winner" });
            await harness.StopEntered.Task.WaitAsync(DiagnosticDeadline);

            // This is an actual contender while the winning completion is still in flight, not
            // a late kill issued after retirement. Await both losing calls to establish quiescence.
            (await harness.Manager.KillAsync(spawned.SubAgentId, ParentSessionId)).ShouldBeFalse();
            await harness.Manager.OnCompletedAsync(spawned.SubAgentId, "duplicate contender");
            var winner = await harness.Manager.GetAsync(spawned.SubAgentId);
            winner.ShouldNotBeNull();
            winner.Status.ShouldBe(SubAgentStatus.Completed);
            winner.ResultSummary.ShouldBe("completion winner");
            runCompletion.IsCompleted.ShouldBeFalse();
            harness.VerifyStopAndWorkspace(spawned, workspaceCalls: Times.Never());
            harness.VerifyDispatch(Times.Once());
            harness.Manager.IsRetiredForTest(spawned.SubAgentId).ShouldBeFalse();

            harness.StopRelease.SetResult();
            await runCompletion.WaitAsync(DiagnosticDeadline);

            (await harness.Manager.GetAsync(spawned.SubAgentId)).ShouldBe(winner);
            harness.Manager.IsRetiredForTest(spawned.SubAgentId).ShouldBeTrue();
            harness.RetiredAtWorkspaceCleanup.ShouldBe(false);
            harness.VerifyStopAndWorkspace(spawned, workspaceCalls: Times.Once());
            harness.VerifyDispatch(Times.Once());
        }
        finally
        {
            harness.ReleaseAll();
            await runCompletion.WaitAsync(DiagnosticDeadline);
        }
    }

    /// <summary>A prompt completing during the winning explicit kill cannot retire or reclaim its child.</summary>
    [Fact]
    public async Task KillAsync_KillWinsWhileStopBlocked_PreservesWinnerAndCleansExactlyOnce()
    {
        var harness = new CleanupHarness();
        var spawned = await harness.SpawnAsync();
        var runCompletion = harness.Manager.WaitForRunCompletionForTestAsync(spawned.SubAgentId);
        Task<bool>? kill = null;
        try
        {
            await harness.PromptEntered.Task.WaitAsync(DiagnosticDeadline);
            kill = harness.Manager.KillAsync(spawned.SubAgentId, ParentSessionId);
            await harness.StopEntered.Task.WaitAsync(DiagnosticDeadline);

            // The controlled provider ignores cancellation and returns a real response while kill
            // owns cleanup. Drain the run loop so its terminal contender has definitely finished.
            harness.PromptResult.SetResult(new AgentResponse { Content = "losing completion" });
            await runCompletion.WaitAsync(DiagnosticDeadline);
            await harness.Manager.OnCompletedAsync(spawned.SubAgentId, "duplicate contender");
            (await harness.Manager.KillAsync(spawned.SubAgentId, ParentSessionId)).ShouldBeFalse();
            var winner = await harness.Manager.GetAsync(spawned.SubAgentId);
            winner.ShouldNotBeNull();
            winner.Status.ShouldBe(SubAgentStatus.Killed);
            winner.ResultSummary.ShouldBe("Sub-agent was killed by parent session.");
            kill.IsCompleted.ShouldBeFalse();
            harness.VerifyStopAndWorkspace(spawned, workspaceCalls: Times.Never());
            harness.VerifyDispatch(Times.Never());
            harness.Manager.IsRetiredForTest(spawned.SubAgentId).ShouldBeFalse();

            harness.StopRelease.SetResult();
            (await kill.WaitAsync(DiagnosticDeadline)).ShouldBeTrue();

            (await harness.Manager.GetAsync(spawned.SubAgentId)).ShouldBe(winner);
            harness.Manager.IsRetiredForTest(spawned.SubAgentId).ShouldBeTrue();
            harness.RetiredAtWorkspaceCleanup.ShouldBe(false);
            harness.VerifyStopAndWorkspace(spawned, workspaceCalls: Times.Once());
            harness.VerifyDispatch(Times.Never());
        }
        finally
        {
            harness.ReleaseAll();
            await runCompletion.WaitAsync(DiagnosticDeadline);
            if (kill is not null)
                _ = await kill.WaitAsync(DiagnosticDeadline);
        }
    }

    private sealed class CleanupHarness
    {
        public TaskCompletionSource PromptEntered { get; } = NewSignal();
        public TaskCompletionSource<AgentResponse> PromptResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } = NewSignal();
        public TaskCompletionSource StopRelease { get; } = NewSignal();
        public ControlledDeadlineClock Clock { get; } = new();
        public DefaultSubAgentManager Manager { get; }
        public bool? RetiredAtWorkspaceCleanup { get; private set; }
        private readonly Mock<IAgentSupervisor> _supervisor = new();
        private readonly Mock<IAgentWorkspaceManager> _workspace = new();
        private readonly Mock<IChannelDispatcher> _dispatcher = new();
        private string? _subAgentId;

        public CleanupHarness()
        {
            var handle = new Mock<IAgentHandle>();
            handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((_, _) =>
                {
                    PromptEntered.TrySetResult();
                    return PromptResult.Task;
                });
            _supervisor.Setup(s => s.GetOrCreateAsync(
                    It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(handle.Object);
            _supervisor.Setup(s => s.StopAsync(
                    It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Returns<AgentId, SessionId, CancellationToken>((_, _, _) =>
                {
                    StopEntered.TrySetResult();
                    return StopRelease.Task;
                });
            _dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var registry = new Mock<IAgentRegistry>();
            registry.Setup(r => r.Get(ParentAgentId)).Returns(new AgentDescriptor
            {
                AgentId = ParentAgentId,
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "copilot"
            });
            var options = new GatewayOptions();
            options.SubAgents.DefaultTimeoutSeconds = 300;
            options.SubAgents.MaxTimeoutSeconds = 300;
            Manager = new DefaultSubAgentManager(
                _supervisor.Object, registry.Object, Mock.Of<IActivityBroadcaster>(),
                _dispatcher.Object, new TestOptionsMonitor<GatewayOptions>(options),
                NullLogger<DefaultSubAgentManager>.Instance,
                workspaceManager: _workspace.Object, timeProvider: Clock);
            _workspace.Setup(w => w.TryCleanupWorkspace(It.IsAny<string>()))
                .Returns(() =>
                {
                    if (_subAgentId is not { } id)
                        throw new InvalidOperationException("Spawn must finish before the prompt is released.");
                    RetiredAtWorkspaceCleanup = Manager.IsRetiredForTest(id);
                    return true;
                });
        }

        public async Task<SubAgentInfo> SpawnAsync()
        {
            var spawned = await Manager.SpawnAsync(new SubAgentSpawnRequest
            {
                ParentAgentId = ParentAgentId,
                ParentSessionId = ParentSessionId,
                Task = "Do controlled background work",
                Mode = new Embody(SubAgentArchetype.General),
                InheritedConversationId = ConversationId.From("inherited-conv"),
                TimeoutSeconds = 300
            });
            _subAgentId = spawned.SubAgentId;
            return spawned;
        }

        public void ReleaseAll()
        {
            PromptResult.TrySetResult(new AgentResponse { Content = "released for drain" });
            StopRelease.TrySetResult();
        }

        public void VerifyStopAndWorkspace(SubAgentInfo spawned, Times workspaceCalls)
        {
            _supervisor.Verify(s => s.StopAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                spawned.ChildSessionId, CancellationToken.None), Times.Once());
            _workspace.Verify(w => w.TryCleanupWorkspace(
                It.Is<string>(id => id.StartsWith("parent-agent--subagent--", StringComparison.Ordinal))),
                workspaceCalls);
        }

        public void VerifyDispatch(Times calls)
            => _dispatcher.Verify(d => d.DispatchAsync(
                It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()), calls);

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // No timer runs on the ambient clock: the absolute deadline fires only at the test's command.
    // The exploration-reserve timer remains parked, so it cannot turn a response into finalization.
    private sealed class ControlledDeadlineClock : TimeProvider
    {
        private ControlledTimer? _absoluteDeadline;
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(callback, state);
            if (dueTime == TimeSpan.FromSeconds(300))
                _absoluteDeadline = timer;
            return timer;
        }

        public void FireAbsoluteDeadline()
        {
            if (_absoluteDeadline is not { } timer)
                throw new InvalidOperationException("The run's absolute deadline has not been scheduled.");
            timer.Fire();
        }

        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                    callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
