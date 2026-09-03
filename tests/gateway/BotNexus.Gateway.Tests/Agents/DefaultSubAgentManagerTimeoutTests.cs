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

public sealed class DefaultSubAgentManagerTimeoutTests
{
    /// <summary>
    /// The run's deadline is scheduled on a virtual clock and fires ONLY because the test advances
    /// it from <c>onPoll</c>. The 300-second budget cannot elapse in real time inside the harness's
    /// 5-second hang guard, so the terminal transition is caused by a signal the test controls and
    /// never by elapsed wall-clock time on a loaded runner (#3216). #3215 converted the sibling
    /// race case for the same reason and left this one on the ambient clock.
    /// </summary>
    [Fact]
    public async Task RunSubAgentAsync_PromptThrowsAfterTimeout_ReportsTimedOut()
    {
        var handle = CreateHandle(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentResponse { Content = "unreachable" };
        });
        var time = new ControllableTimeProvider();
        var (manager, dispatcher, dispatched) = CreateManager(handle, time, timeoutSecondsBudget: 300);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, time, advanceBy: TimeSpan.FromSeconds(300), timeoutSeconds: 300);

        AssertTimedOut(result, dispatcher, timeoutSeconds: 300);
    }

    /// <summary>
    /// Same construction as the throwing case: a 300-second deadline on a virtual clock, reached
    /// only by the test's explicit advance (#3216). The classification under test is unchanged -
    /// an empty response returned AFTER cancellation must still resolve to TimedOut rather than
    /// the Failed-on-empty path.
    /// </summary>
    [Fact]
    public async Task RunSubAgentAsync_PromptReturnsEmptyAfterTimeout_ReportsTimedOut()
    {
        var handle = CreateHandle(async token =>
        {
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => cancellationObserved.SetResult());
            await cancellationObserved.Task;
            return new AgentResponse { Content = string.Empty };
        });
        var time = new ControllableTimeProvider();
        var (manager, dispatcher, dispatched) = CreateManager(handle, time, timeoutSecondsBudget: 300);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, time, advanceBy: TimeSpan.FromSeconds(300), timeoutSeconds: 300);

        AssertTimedOut(result, dispatcher, timeoutSeconds: 300);
    }

    [Fact]
    public async Task RunSubAgentAsync_EmptyResponseBeforeTimeout_ReportsFailed()
    {
        // The deadline is scheduled on a virtual clock that is never advanced, so it CANNOT fire.
        // The classification under test is therefore decided by the response content alone and no
        // longer by whether a synchronous delegate beats a real 1s timer on a loaded runner (#2979).
        var handle = CreateHandle(_ => Task.FromResult(new AgentResponse { Content = "  " }));
        var (manager, dispatcher, dispatched) = CreateManager(handle, new ControllableTimeProvider());

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched);

        result.Status.ShouldBe(SubAgentStatus.Failed);
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain("empty final response");
        VerifyDiagnostic(dispatcher, "failed", "empty final response");
    }

    [Fact]
    public async Task RunSubAgentAsync_NonEmptyResponseBeforeTimeout_ReportsCompleted()
    {
        // Same deterministic construction as the Failed case: an unadvanced virtual deadline.
        var handle = CreateHandle(_ => Task.FromResult(new AgentResponse { Content = "Implemented the fix." }));
        var (manager, dispatcher, dispatched) = CreateManager(handle, new ControllableTimeProvider());

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.ResultSummary.ShouldBe("Implemented the fix.");
        VerifyDiagnostic(dispatcher, "completed", "Implemented the fix.");
    }

    /// <summary>
    /// The race under test is preserved verbatim: the handle still returns an EMPTY response, and
    /// only after observing cancellation plus a yield, so the classification must still resolve to
    /// TimedOut rather than the Failed-on-empty path. What changed (#3215) is the clock. The
    /// deadline is scheduled on an injected <see cref="TimeProvider"/> with a <b>300-second</b>
    /// budget, so no real timer can fire inside the harness's 5-second poll window; the run reaches
    /// a terminal state only because <c>onPoll</c> advances the virtual clock. Previously this case
    /// passed <c>timeProvider: null</c> with a 1-second budget, so reaching terminal depended on a
    /// real timer plus continuation scheduling landing inside 5 wall-clock seconds on a shared
    /// 4-CPU runner - the flake reported in #3215.
    /// </summary>
    [Fact]
    public async Task RunSubAgentAsync_TimeoutRacesWithEmptyPromptReturn_NeverReportsCompleted()
    {
        var handle = CreateHandle(async token =>
        {
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => cancellationObserved.SetResult());
            await cancellationObserved.Task;
            await Task.Yield();
            return new AgentResponse { Content = string.Empty };
        });
        var time = new ControllableTimeProvider();
        var (manager, dispatcher, dispatched) = CreateManager(handle, time, timeoutSecondsBudget: 300);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, time, advanceBy: TimeSpan.FromSeconds(300), timeoutSeconds: 300);

        AssertTimedOut(result, dispatcher, timeoutSeconds: 300);
        result.Status.ShouldNotBe(SubAgentStatus.Completed);
    }

    /// <summary>
    /// Pins the seam the fix depends on: the run's deadline must be scheduled on the INJECTED
    /// <see cref="TimeProvider"/>, not on the ambient <c>CancelAfter</c> timer.
    /// <para>
    /// The budget is <b>300 seconds</b> deliberately. A real timer with that budget cannot fire
    /// inside the harness's 5-second poll window, so the ONLY way this run can reach a terminal
    /// state is a virtual advance firing a timer the provider itself owns. Reverting the production
    /// code to <c>new CancellationTokenSource()</c> + <c>CancelAfter</c> makes this test fail with
    /// "Sub-agent did not reach a terminal state" - mutation-verified. An earlier version of this
    /// test used a 1-second budget and SURVIVED that mutation, because a real 1s timer fires inside
    /// the poll window regardless of which clock scheduled it: it proved nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunSubAgentAsync_TimeoutIsScheduledOnInjectedTimeProvider_VirtualAdvanceTimesOut()
    {
        var handle = CreateHandle(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentResponse { Content = "unreachable" };
        });
        var time = new ControllableTimeProvider();
        var (manager, dispatcher, dispatched) = CreateManager(handle, time, timeoutSecondsBudget: 300);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, time, advanceBy: TimeSpan.FromSeconds(300), timeoutSeconds: 300);

        result.Status.ShouldBe(SubAgentStatus.TimedOut);
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain("timed out after 300 seconds");
        VerifyDiagnostic(dispatcher, "timed out", "timed out after 300 seconds");
    }

    /// <summary>
    /// Spawns a run and returns its terminal snapshot, waiting on the SIGNAL that marks the run
    /// settled - the completion diagnostic the manager dispatches from <c>OnCompletedAsync</c>,
    /// raised only after the record's status has already been flipped out of
    /// <see cref="SubAgentStatus.Running"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This helper used to poll <c>GetAsync</c> every 20 ms inside a 5-second wall-clock budget and
    /// throw a <c>TimeoutException</c> when the budget elapsed. That budget was a race, not an
    /// assertion: on a loaded 4-CPU container the run could simply fail to be scheduled within 5 s
    /// and the test reddened without any production behaviour being wrong - observed on remote gate
    /// run 20260903181859-98cba615 against a diff that touched no sub-agent code at all (#3820).
    /// </para>
    /// <para>
    /// Waiting on the dispatch signal removes the budget from the success path entirely: the wait
    /// completes when the run settles, however long the runner took to get there. The virtual clock
    /// advance that CAUSES a timeout case to settle is applied once, immediately after
    /// <c>SpawnAsync</c> returns - safe because the deadline's <c>CancellationTokenSource</c> is
    /// constructed synchronously inside <c>SpawnAsync</c>, before the run task is queued, so the
    /// virtual timer provably exists by the time it is advanced.
    /// </para>
    /// <para>
    /// The <see cref="HangGuard"/> bound that remains is a HANG guard and nothing else. It is an
    /// order of magnitude larger than any scheduling delay a runner can plausibly impose, so it can
    /// only be reached by a run that never settles at all - and its failure is then a genuine
    /// defect, not a lost race.
    /// </para>
    /// </remarks>
    private static async Task<SubAgentInfo> SpawnAndAwaitTerminalAsync(
        DefaultSubAgentManager manager,
        TaskCompletionSource dispatched,
        ControllableTimeProvider? time = null,
        TimeSpan? advanceBy = null,
        int timeoutSeconds = 1)
    {
        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            TimeoutSeconds = timeoutSeconds,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        });

        if (time is not null && advanceBy is { } delta)
            time.Advance(delta);

        await dispatched.Task.WaitAsync(HangGuard);

        var current = await manager.GetAsync(spawned.SubAgentId);
        current.ShouldNotBeNull();
        current.Status.ShouldNotBe(SubAgentStatus.Running);
        return current;
    }

    /// <summary>
    /// Upper bound on a run that has genuinely deadlocked. Never reached on the success path, which
    /// is signal-driven; see <see cref="SpawnAndAwaitTerminalAsync"/>.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Asserts the timed-out terminal state and its dispatched diagnostic. The budget is a
    /// parameter only so a case running on a virtual clock can use a budget large enough that no
    /// real timer could fire (#3215); the assertions themselves are identical for every caller and
    /// the expected diagnostic text is still derived from the budget, so it cannot be satisfied by
    /// a timeout of the wrong length.
    /// </summary>
    private static void AssertTimedOut(
        SubAgentInfo result,
        Mock<IChannelDispatcher> dispatcher,
        int timeoutSeconds = 1)
    {
        var expected = $"timed out after {timeoutSeconds} {(timeoutSeconds == 1 ? "second" : "seconds")}";
        result.Status.ShouldBe(SubAgentStatus.TimedOut);
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain(expected);
        VerifyDiagnostic(dispatcher, "timed out", expected);
    }

    private static void VerifyDiagnostic(Mock<IChannelDispatcher> dispatcher, string status, string diagnostic)
    {
        dispatcher.Verify(d => d.DispatchAsync(
            It.Is<InboundMessage>(message =>
                message.Content.Contains(status, StringComparison.OrdinalIgnoreCase) &&
                message.Content.Contains(diagnostic, StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IAgentHandle> CreateHandle(Func<CancellationToken, Task<AgentResponse>> prompt)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("child-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, token) => prompt(token));
        return handle;
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose clock only moves when a test moves it. Timers created
    /// through it fire solely from <see cref="Advance"/>, so a deadline scheduled on this provider
    /// is unreachable until the test chooses to reach it. That is what makes the non-timeout
    /// classification tests independent of runner load (#2979).
    /// </summary>
    private sealed class ControllableTimeProvider : TimeProvider
    {
        private readonly List<VirtualTimer> _timers = [];
        private readonly Lock _gate = new();
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new VirtualTimer(this, callback, state, dueTime);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        /// <summary>Moves the virtual clock forward and fires every timer now due.</summary>
        public void Advance(TimeSpan delta)
        {
            VirtualTimer[] due;
            lock (_gate)
            {
                _now = _now.Add(delta);
                due = [.. _timers.Where(t => t.IsDueAt(_now))];
            }

            foreach (var timer in due)
                timer.Fire();
        }

        internal void Remove(VirtualTimer timer)
        {
            lock (_gate)
                _timers.Remove(timer);
        }

        internal sealed class VirtualTimer(
            ControllableTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow().Add(dueTime);
            private int _fired;

            public bool IsDueAt(DateTimeOffset now) => _dueAt is { } due && now >= due;

            public void Fire()
            {
                if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
                    return;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow().Add(dueTime);
                return true;
            }

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Builds the manager under test plus the completion SIGNAL the tests wait on. The signal is
    /// completed from the dispatcher stub, which the manager invokes only after the record's status
    /// has already left <see cref="SubAgentStatus.Running"/> - so observing it is equivalent to
    /// observing a terminal transition, without any wall-clock poll (#3820).
    /// </summary>
    private static (DefaultSubAgentManager Manager, Mock<IChannelDispatcher> Dispatcher, TaskCompletionSource Dispatched) CreateManager(
        Mock<IAgentHandle> handle,
        TimeProvider? timeProvider = null,
        int timeoutSecondsBudget = 1)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.IsAny<SessionId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("parent-agent"))).Returns(new AgentDescriptor
        {
            AgentId = AgentId.From("parent-agent"),
            DisplayName = "Parent Agent",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot"
        });

        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback(() => dispatched.TrySetResult())
            .Returns(Task.CompletedTask);

        var options = new GatewayOptions();
        options.SubAgents.MaxTimeoutSeconds = timeoutSecondsBudget;
        options.SubAgents.DefaultTimeoutSeconds = timeoutSecondsBudget;

        return (new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<DefaultSubAgentManager>.Instance,
            timeProvider: timeProvider), dispatcher, dispatched);
    }
}
