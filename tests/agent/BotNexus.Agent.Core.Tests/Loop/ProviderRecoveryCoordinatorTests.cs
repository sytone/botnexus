using BotNexus.Agent.Core.Loop;

namespace BotNexus.Agent.Core.Tests.Loop;

public sealed class ProviderRecoveryCoordinatorTests
{
    private static readonly ProviderRecoveryScope Scope = new("github-copilot", "profile-a");

    [Fact]
    public async Task QueuedCircuitAdmission_ReportsBoundedLifecycleWithoutCredentialIdentity()
    {
        var clock = new ManualClock();
        var observed = new List<ProviderRecoveryObservation>();
        var coordinator = CreateCoordinator(clock, threshold: 1, maxQueue: 1, observe: observed.Add);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None);
        opener.ReportTransientFailure(null);

        var queued = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));
        var probe = await queued;
        probe.ReportSuccess();

        observed.ShouldContain(observation =>
            observation.Stage == ProviderRecoveryStage.CircuitOpened
            && observation.Provider == "github-copilot"
            && observation.State == ProviderRecoveryState.Open);
        observed.ShouldContain(observation =>
            observation.Stage == ProviderRecoveryStage.AdmissionQueued
            && observation.QueueLength == 1);
        observed.ShouldContain(observation =>
            observation.Stage == ProviderRecoveryStage.ProbeAdmitted
            && observation.State == ProviderRecoveryState.HalfOpen);
        observed.ShouldContain(observation =>
            observation.Stage == ProviderRecoveryStage.Recovered
            && observation.State == ProviderRecoveryState.Closed);
        observed.All(observation => observation.AuthProfile == null && observation.ProviderError == null).ShouldBeTrue();
    }

    [Fact]
    public async Task ThreeIndependentFailures_OpenCircuitAndAdmitOnlyOneProbe()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 3, maxQueue: 3);
        var incidents = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var leases = await Task.WhenAll(incidents.Select(id => coordinator.AcquireAsync(
            Scope, id, TimeSpan.FromSeconds(5), CancellationToken.None).AsTask()));

        foreach (var lease in leases)
        {
            lease.ReportTransientFailure(TimeSpan.FromSeconds(2));
        }

        coordinator.GetSnapshot(Scope).State.ShouldBe(ProviderRecoveryState.Open);
        clock.Advance(TimeSpan.FromSeconds(2));
        var probe = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None);
        probe.IsProbe.ShouldBeTrue();
        var competingProbe = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None).AsTask();
        competingProbe.IsCompleted.ShouldBeFalse("half-open recovery must be single-flight");

        probe.ReportSuccess();
        var released = await competingProbe;
        released.IsProbe.ShouldBeFalse();
        released.ReportSuccess();
    }

    [Fact]
    public async Task RepeatedFailuresFromOneRun_DoNotOpenCircuit()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 3);
        var incident = Guid.NewGuid();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var lease = await coordinator.AcquireAsync(Scope, incident, TimeSpan.FromSeconds(5), CancellationToken.None);
            lease.ReportTransientFailure(null);
        }

        var snapshot = coordinator.GetSnapshot(Scope);
        snapshot.State.ShouldBe(ProviderRecoveryState.Closed);
        snapshot.DistinctTransientFailures.ShouldBe(1);
    }

    [Fact]
    public async Task StaleSuccess_CannotCloseReopenedGeneration()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1);
        var original = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None);
        var staleCall = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None);
        original.ReportTransientFailure(null);
        clock.Advance(TimeSpan.FromSeconds(1));
        var firstProbe = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None);
        firstProbe.ReportTransientFailure(null);
        var reopenedGeneration = coordinator.GetSnapshot(Scope).Generation;

        staleCall.ReportSuccess();

        var snapshot = coordinator.GetSnapshot(Scope);
        snapshot.State.ShouldBe(ProviderRecoveryState.Open);
        snapshot.Generation.ShouldBe(reopenedGeneration);
    }

    [Fact]
    public async Task QueuedAdmission_CancellationHasTypedOutcome()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1, maxQueue: 1);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None);
        opener.ReportTransientFailure(null);
        using var cancellation = new CancellationTokenSource();
        var queued = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), cancellation.Token).AsTask();
        cancellation.Cancel();

        await Should.ThrowAsync<ProviderRecoveryCancelledException>(() => queued);
    }

    [Fact]
    public async Task FullQueue_RejectsWithTypedSaturationOutcome()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1, maxQueue: 1);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None);
        opener.ReportTransientFailure(null);
        var queued = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None).AsTask();

        await Should.ThrowAsync<ProviderRecoveryQueueFullException>(async () =>
            await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None));
        queued.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task QueuedAdmission_DeadlineHasTypedOutcome()
    {
        var coordinator = new ProviderRecoveryCoordinator(new ProviderRecoveryOptions(
            FailureThreshold: 1,
            FailureWindow: TimeSpan.FromMinutes(1),
            OpenDuration: TimeSpan.FromMinutes(1),
            MaxOpenDuration: TimeSpan.FromMinutes(1),
            MaxQueueLength: 1,
            MaxConcurrentCalls: 1));
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(1), CancellationToken.None);
        opener.ReportTransientFailure(null);

        await Should.ThrowAsync<ProviderRecoveryDeadlineExceededException>(async () =>
            await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }

    [Fact]
    public async Task RetryAfter_IsRecoveryFloorAndCappedByPolicy()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1, maxOpen: TimeSpan.FromSeconds(5));
        var lease = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None);
        lease.ReportTransientFailure(TimeSpan.FromMinutes(10));

        coordinator.GetSnapshot(Scope).NextProbeAt.ShouldBe(clock.UtcNow + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NonTransientFailure_DoesNotOpenCircuit()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1);
        var lease = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None);
        lease.ReportNonTransientFailure();

        coordinator.GetSnapshot(Scope).State.ShouldBe(ProviderRecoveryState.Closed);
    }

    [Fact]
    public async Task ArrivalAtCooldownBoundary_AdmitsQueuedCallersInOrderBeforeNewcomer()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1, maxQueue: 3, maxConcurrent: 1);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None);
        opener.ReportTransientFailure(null);
        var generation = coordinator.GetSnapshot(Scope).Generation;
        var first = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();
        var second = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();
        var third = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        clock.Advance(TimeSpan.FromSeconds(1), dispatch: false);
        var newcomer = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        first.IsCompletedSuccessfully.ShouldBeTrue("the oldest queued call owns the available probe even before wake dispatch");
        second.IsCompleted.ShouldBeFalse();
        third.IsCompleted.ShouldBeFalse();
        newcomer.IsCompleted.ShouldBeFalse("draining the head frees one queue slot, not another probe");
        var probe = await first;
        probe.IsProbe.ShouldBeTrue();
        coordinator.GetSnapshot(Scope).Generation.ShouldBe(generation);
        coordinator.GetSnapshot(Scope).InFlightCalls.ShouldBe(1);
        coordinator.GetSnapshot(Scope).QueuedCalls.ShouldBe(3);

        probe.ReportSuccess();
        var secondLease = await second;
        secondLease.IsProbe.ShouldBeFalse();
        third.IsCompleted.ShouldBeFalse();
        newcomer.IsCompleted.ShouldBeFalse();
        secondLease.ReportSuccess();
        var thirdLease = await third;
        newcomer.IsCompleted.ShouldBeFalse();
        thirdLease.ReportSuccess();
        (await newcomer).ReportSuccess();
        coordinator.GetSnapshot(Scope).InFlightCalls.ShouldBe(0);
        coordinator.GetSnapshot(Scope).Generation.ShouldBe(generation + 1);
    }

    [Fact]
    public async Task ArrivalAtCooldownBoundary_SkipsCompletedHeadAndMakesLiveHeadProgress()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1, maxQueue: 3);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None);
        opener.ReportTransientFailure(null);
        using var cancellation = new CancellationTokenSource();
        var cancelled = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), cancellation.Token).AsTask();
        var live = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();
        cancellation.Cancel();
        await Should.ThrowAsync<ProviderRecoveryCancelledException>(() => cancelled);

        clock.Advance(TimeSpan.FromSeconds(1), dispatch: false);
        var newcomer = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        live.IsCompletedSuccessfully.ShouldBeTrue();
        newcomer.IsCompleted.ShouldBeFalse();
        var probe = await live;
        probe.IsProbe.ShouldBeTrue();
        probe.ReportSuccess();
        (await newcomer).ReportSuccess();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrivalAtCooldownBoundary_EmptyOrFullyCancelledQueueAllowsImmediateProbe(bool cancelQueued)
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1, maxQueue: 1);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None);
        opener.ReportTransientFailure(null);
        using var cancellation = new CancellationTokenSource();
        if (cancelQueued)
        {
            var queued = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), cancellation.Token).AsTask();
            cancellation.Cancel();
            await Should.ThrowAsync<ProviderRecoveryCancelledException>(() => queued);
        }

        clock.Advance(TimeSpan.FromSeconds(1), dispatch: false);
        var admission = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None);
        admission.IsCompletedSuccessfully.ShouldBeTrue();
        var probe = await admission;
        probe.IsProbe.ShouldBeTrue();
        coordinator.GetSnapshot(Scope).QueuedCalls.ShouldBe(0);
        probe.ReportSuccess();
    }

    [Fact]
    public async Task ArrivalAtCooldownBoundary_FullQueueDrainsOneSlotThenRejectsFurtherArrival()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1, maxQueue: 1);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None);
        opener.ReportTransientFailure(null);
        var head = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();
        await Should.ThrowAsync<ProviderRecoveryQueueFullException>(async () =>
            await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None));
        head.IsCompleted.ShouldBeFalse();

        clock.Advance(TimeSpan.FromSeconds(1), dispatch: false);
        var newcomer = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();
        head.IsCompletedSuccessfully.ShouldBeTrue();
        newcomer.IsCompleted.ShouldBeFalse();
        await Should.ThrowAsync<ProviderRecoveryQueueFullException>(async () =>
            await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None));
        coordinator.GetSnapshot(Scope).InFlightCalls.ShouldBe(1);
        (await head).ReportSuccess();
        (await newcomer).ReportSuccess();
    }

    [Fact]
    public async Task ArrivalAtCooldownBoundary_ExpiredHeadCannotTakeProbeBeforeDeadlineWake()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1, maxQueue: 2);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None);
        opener.ReportTransientFailure(null);
        var expired = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(1), CancellationToken.None).AsTask();
        var live = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        clock.Advance(TimeSpan.FromSeconds(1), dispatch: false);
        var newcomer = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        expired.IsCompleted.ShouldBeTrue("deadline authority must not depend on dispatching its delay continuation");
        await Should.ThrowAsync<ProviderRecoveryDeadlineExceededException>(() => expired);
        live.IsCompletedSuccessfully.ShouldBeTrue();
        newcomer.IsCompleted.ShouldBeFalse();
        (await live).ReportSuccess();
        (await newcomer).ReportSuccess();
    }

    [Fact]
    public async Task ArrivalAtCooldownBoundary_CancelledHeadCannotTakeProbeBeforeCancellationWake()
    {
        var clock = new ManualClock { HoldCancellation = true };
        var coordinator = CreateCoordinator(clock, threshold: 1, maxQueue: 2);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None);
        opener.ReportTransientFailure(null);
        using var cancellation = new CancellationTokenSource();
        var cancelled = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), cancellation.Token).AsTask();
        var live = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();
        cancellation.Cancel();
        cancelled.IsCompleted.ShouldBeFalse("the delay seam holds cancellation wake dispatch");

        clock.Advance(TimeSpan.FromSeconds(1), dispatch: false);
        var newcomer = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        cancelled.IsCompleted.ShouldBeTrue();
        var exception = await Should.ThrowAsync<ProviderRecoveryCancelledException>(() => cancelled);
        exception.CancellationToken.ShouldBe(cancellation.Token);
        live.IsCompletedSuccessfully.ShouldBeTrue();
        newcomer.IsCompleted.ShouldBeFalse();
        (await live).ReportSuccess();
        (await newcomer).ReportSuccess();
    }

    [Fact]
    public async Task ArrivalAtCooldownBoundary_InvalidNewcomerCannotDisplaceQueuedProbe()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None);
        opener.ReportTransientFailure(null);
        var head = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromSeconds(1), dispatch: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), cancellation.Token));
        await Should.ThrowAsync<ProviderRecoveryDeadlineExceededException>(async () =>
            await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.Zero, CancellationToken.None));
        head.IsCompleted.ShouldBeFalse("rejected callers do not change admission state");
        clock.DispatchDue();
        var probe = await head;
        probe.IsProbe.ShouldBeTrue();
        probe.ReportSuccess();
    }

    [Fact]
    public async Task ArrivalAtCooldownBoundary_QueuedProbePreservesGenerationAndStaleCallFence()
    {
        var clock = new ManualClock();
        var coordinator = CreateCoordinator(clock, threshold: 1);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None);
        var stale = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None);
        opener.ReportTransientFailure(null);
        var head = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();
        var generation = coordinator.GetSnapshot(Scope).Generation;
        clock.Advance(TimeSpan.FromSeconds(1), dispatch: false);
        var newcomer = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        head.IsCompletedSuccessfully.ShouldBeTrue();
        var probe = await head;
        probe.IsProbe.ShouldBeTrue();
        stale.ReportSuccess();
        coordinator.GetSnapshot(Scope).State.ShouldBe(ProviderRecoveryState.HalfOpen);
        coordinator.GetSnapshot(Scope).Generation.ShouldBe(generation);
        newcomer.IsCompleted.ShouldBeFalse();
        probe.ReportTransientFailure(null);
        coordinator.GetSnapshot(Scope).Generation.ShouldBe(generation + 1);
        coordinator.GetSnapshot(Scope).State.ShouldBe(ProviderRecoveryState.Open);
        newcomer.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(2), dispatch: false);
        var nextArrival = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();
        newcomer.IsCompletedSuccessfully.ShouldBeTrue();
        nextArrival.IsCompleted.ShouldBeFalse();
        var nextProbe = await newcomer;
        nextProbe.IsProbe.ShouldBeTrue();
        nextProbe.ReportSuccess();
        (await nextArrival).ReportSuccess();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeObserver_CancelsHeadAndReentersAdmissionWithoutLosingLease(bool useRequestObserver)
    {
        var clock = new ManualClock { HoldCancellation = true };
        using var cancellation = new CancellationTokenSource();
        ProviderRecoveryCoordinator? coordinator = null;
        Task<ProviderRecoveryLease>? first = null;
        Task<ProviderRecoveryLease>? reentrant = null;
        ProviderRecoverySnapshot? observerSnapshot = null;
        var completedBeforeCancellation = false;
        var probeObservations = 0;
        void Observe(ProviderRecoveryObservation observation)
        {
            if (observation.Stage != ProviderRecoveryStage.ProbeAdmitted)
            {
                return;
            }

            probeObservations++;
            var current = coordinator ?? throw new InvalidOperationException("Coordinator must exist before observation.");
            completedBeforeCancellation = first?.IsCompletedSuccessfully == true;
            observerSnapshot = current.GetSnapshot(Scope);
            cancellation.Cancel();
            // Hold cancellation wakes so the nested drain itself exposes any uncommitted head.
            reentrant = current.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();
        }

        coordinator = CreateCoordinator(clock, threshold: 1, maxQueue: 2, maxConcurrent: 1,
            observe: useRequestObserver ? null : Observe);
        var opener = await coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None);
        opener.ReportTransientFailure(null);
        var generation = coordinator.GetSnapshot(Scope).Generation;
        first = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), cancellation.Token,
            observe: useRequestObserver ? Observe : null).AsTask();

        clock.Advance(TimeSpan.FromSeconds(1), dispatch: false);
        var newcomer = coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        probeObservations.ShouldBe(1);
        cancellation.IsCancellationRequested.ShouldBeTrue();
        completedBeforeCancellation.ShouldBeTrue("admission commits before diagnostic observers can cancel or reenter");
        first.IsCompletedSuccessfully.ShouldBeTrue("observer cancellation occurs after admission, not while queued");
        observerSnapshot.ShouldNotBeNull();
        observerSnapshot.State.ShouldBe(ProviderRecoveryState.HalfOpen);
        observerSnapshot.InFlightCalls.ShouldBe(1);
        observerSnapshot.QueuedCalls.ShouldBe(0);
        _ = reentrant.ShouldNotBeNull();
        reentrant.IsCompleted.ShouldBeFalse();
        newcomer.IsCompleted.ShouldBeFalse();
        coordinator.GetSnapshot(Scope).QueuedCalls.ShouldBe(2);
        coordinator.GetSnapshot(Scope).Generation.ShouldBe(generation);

        var probe = await first;
        probe.IsProbe.ShouldBeTrue();
        probe.ReportSuccess();
        var next = await reentrant;
        next.IsProbe.ShouldBeFalse();
        newcomer.IsCompleted.ShouldBeFalse("the reentrant caller keeps its FIFO place");
        next.ReportSuccess();
        (await newcomer).ReportSuccess();
        coordinator.GetSnapshot(Scope).InFlightCalls.ShouldBe(0);
        coordinator.GetSnapshot(Scope).QueuedCalls.ShouldBe(0);
        coordinator.GetSnapshot(Scope).State.ShouldBe(ProviderRecoveryState.Closed);
    }

    private static ProviderRecoveryCoordinator CreateCoordinator(
        ManualClock clock,
        int threshold,
        int maxQueue = 8,
        TimeSpan? maxOpen = null,
        Action<ProviderRecoveryObservation>? observe = null,
        int maxConcurrent = 8)
        => new(
            new ProviderRecoveryOptions(
                FailureThreshold: threshold,
                FailureWindow: TimeSpan.FromMinutes(1),
                OpenDuration: TimeSpan.FromSeconds(1),
                MaxOpenDuration: maxOpen ?? TimeSpan.FromSeconds(30),
                MaxQueueLength: maxQueue,
                MaxConcurrentCalls: maxConcurrent),
            clock.GetUtcNow,
            clock.DelayAsync,
            observe);

    private sealed class ManualClock
    {
        private readonly object _sync = new();
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Completion)> _delays = [];

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_sync)
                {
                    return _utcNow;
                }
            }
        }

        public bool HoldCancellation { get; init; }

        public DateTimeOffset GetUtcNow() => UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _delays.Add((_utcNow + delay, completion));
            }

            cancellationToken.Register(() =>
            {
                if (!HoldCancellation)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
            });
            return completion.Task;
        }

        public void Advance(TimeSpan duration, bool dispatch = true)
        {
            lock (_sync)
            {
                _utcNow += duration;
            }

            if (dispatch)
            {
                DispatchDue();
            }
        }

        public void DispatchDue()
        {
            List<TaskCompletionSource> ready;
            lock (_sync)
            {
                ready = _delays.Where(item => item.Due <= _utcNow).Select(item => item.Completion).ToList();
                _delays.RemoveAll(item => item.Due <= _utcNow);
            }

            foreach (var completion in ready)
            {
                completion.TrySetResult();
            }
        }
    }
}
