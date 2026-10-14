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

    private static ProviderRecoveryCoordinator CreateCoordinator(
        ManualClock clock,
        int threshold,
        int maxQueue = 8,
        TimeSpan? maxOpen = null,
        Action<ProviderRecoveryObservation>? observe = null)
        => new(
            new ProviderRecoveryOptions(
                FailureThreshold: threshold,
                FailureWindow: TimeSpan.FromMinutes(1),
                OpenDuration: TimeSpan.FromSeconds(1),
                MaxOpenDuration: maxOpen ?? TimeSpan.FromSeconds(30),
                MaxQueueLength: maxQueue,
                MaxConcurrentCalls: 8),
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

            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public void Advance(TimeSpan duration)
        {
            List<TaskCompletionSource> ready;
            lock (_sync)
            {
                _utcNow += duration;
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
