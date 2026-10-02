namespace BotNexus.Agent.Core.Loop;

/// <summary>Identifies the bounded provider and credential scope sharing transient recovery state.</summary>
/// <param name="Provider">The provider implementation identifier.</param>
/// <param name="AuthProfile">The credential profile identifier, or an empty string for ambient credentials.</param>
public readonly record struct ProviderRecoveryScope(string Provider, string AuthProfile);

/// <summary>Describes the deterministic state of a provider recovery circuit.</summary>
public enum ProviderRecoveryState
{
    /// <summary>Ordinary calls are admitted up to the configured concurrency bound.</summary>
    Closed,
    /// <summary>Calls wait in the bounded queue until the recovery floor expires.</summary>
    Open,
    /// <summary>Exactly one recovery probe is in flight.</summary>
    HalfOpen,
}

/// <summary>Identifies a bounded provider-recovery lifecycle transition.</summary>
public enum ProviderRecoveryStage
{
    /// <summary>A transient provider failure will be retried after a bounded delay.</summary>
    RetryScheduled,
    /// <summary>The shared provider circuit opened after the configured independent-failure threshold.</summary>
    CircuitOpened,
    /// <summary>A provider call entered the bounded admission queue.</summary>
    AdmissionQueued,
    /// <summary>The single current-generation half-open recovery probe was admitted.</summary>
    ProbeAdmitted,
    /// <summary>A retry or half-open probe restored provider availability.</summary>
    Recovered,
    /// <summary>The bounded per-turn retry budget was exhausted.</summary>
    Exhausted,
}

/// <summary>
/// Bounded provider-recovery telemetry. Credential identity, incident identity, prompts, sessions,
/// and raw provider error text are deliberately excluded.
/// </summary>
public sealed record ProviderRecoveryObservation(
    ProviderRecoveryStage Stage,
    string Provider,
    ProviderRecoveryState State,
    long Generation,
    int InFlightCalls,
    int QueueLength,
    DateTimeOffset? NextProbeAt,
    int? Attempt = null,
    int? MaxAttempts = null,
    TimeSpan? Delay = null,
    string? AuthProfile = null,
    string? ProviderError = null);

/// <summary>Sets bounded process-wide provider recovery policy.</summary>
/// <param name="FailureThreshold">Distinct run failures required within the window to open the circuit.</param>
/// <param name="FailureWindow">Rolling interval in which distinct failures contribute to the threshold.</param>
/// <param name="OpenDuration">Minimum delay before a half-open probe when no longer provider guidance exists.</param>
/// <param name="MaxOpenDuration">Hard ceiling for local cooldown and provider Retry-After guidance.</param>
/// <param name="MaxQueueLength">Maximum deferred admissions per provider scope.</param>
/// <param name="MaxConcurrentCalls">Maximum provider calls in flight per scope while closed.</param>
public sealed record ProviderRecoveryOptions(
    int FailureThreshold = 3,
    TimeSpan? FailureWindow = null,
    TimeSpan? OpenDuration = null,
    TimeSpan? MaxOpenDuration = null,
    int MaxQueueLength = 64,
    int MaxConcurrentCalls = 32)
{
    internal TimeSpan EffectiveFailureWindow => FailureWindow is { } value && value > TimeSpan.Zero
        ? value
        : TimeSpan.FromSeconds(30);

    internal TimeSpan EffectiveOpenDuration => OpenDuration is { } value && value > TimeSpan.Zero
        ? value
        : TimeSpan.FromSeconds(2);

    internal TimeSpan EffectiveMaxOpenDuration => MaxOpenDuration is { } value && value > TimeSpan.Zero
        ? value
        : TimeSpan.FromMilliseconds(Configuration.AgentLoopConfig.DefaultMaxRetryDelayMs);
}

/// <summary>Provides a bounded snapshot for diagnostics and deterministic transition tests.</summary>
/// <param name="State">Current circuit state.</param>
/// <param name="Generation">Monotonic fence changed whenever the circuit opens or closes.</param>
/// <param name="DistinctTransientFailures">Distinct run failures still inside the rolling window.</param>
/// <param name="InFlightCalls">Currently admitted provider calls.</param>
/// <param name="QueuedCalls">Calls waiting for admission.</param>
/// <param name="NextProbeAt">Earliest half-open probe instant, when open.</param>
public sealed record ProviderRecoverySnapshot(
    ProviderRecoveryState State,
    long Generation,
    int DistinctTransientFailures,
    int InFlightCalls,
    int QueuedCalls,
    DateTimeOffset? NextProbeAt);

/// <summary>Coordinates transient recovery and bounded admission across all runs sharing a provider scope.</summary>
public interface IProviderRecoveryCoordinator
{
    /// <summary>Waits for bounded admission to one provider model call.</summary>
    /// <param name="scope">Provider and credential scope sharing health state.</param>
    /// <param name="incidentId">Stable identifier for the current model-call retry loop.</param>
    /// <param name="maxWait">Authoritative admission deadline.</param>
    /// <param name="cancellationToken">Caller cancellation, which remains authoritative while queued.</param>
    ValueTask<ProviderRecoveryLease> AcquireAsync(
        ProviderRecoveryScope scope,
        Guid incidentId,
        TimeSpan maxWait,
        CancellationToken cancellationToken,
        Action<ProviderRecoveryObservation>? observe = null);

    /// <summary>Returns bounded current state without exposing provider error bodies or run identifiers.</summary>
    ProviderRecoverySnapshot GetSnapshot(ProviderRecoveryScope scope);
}

/// <summary>Base typed temporary-unavailability outcome from provider recovery admission.</summary>
public abstract class ProviderRecoveryException : Exception
{
    /// <summary>Initializes a typed provider recovery outcome.</summary>
    protected ProviderRecoveryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Indicates that the bounded provider admission queue is full.</summary>
public sealed class ProviderRecoveryQueueFullException : ProviderRecoveryException
{
    /// <summary>Creates the saturation outcome.</summary>
    public ProviderRecoveryQueueFullException(string provider)
        : base($"Provider recovery queue for '{provider}' is full.")
    {
    }
}

/// <summary>Indicates that provider admission did not occur before its deadline.</summary>
public sealed class ProviderRecoveryDeadlineExceededException : ProviderRecoveryException
{
    /// <summary>Creates the deadline outcome.</summary>
    public ProviderRecoveryDeadlineExceededException(string provider)
        : base($"Provider recovery admission deadline expired for '{provider}'.")
    {
    }
}

/// <summary>Indicates that caller cancellation removed a queued provider admission.</summary>
public sealed class ProviderRecoveryCancelledException : OperationCanceledException
{
    /// <summary>Creates the cancellation outcome while retaining the authoritative caller token.</summary>
    public ProviderRecoveryCancelledException(string provider, CancellationToken cancellationToken)
        : base($"Provider recovery admission was cancelled for '{provider}'.", cancellationToken)
    {
    }
}

/// <summary>Represents one admitted provider model call and its generation fence.</summary>
public sealed class ProviderRecoveryLease
{
    private readonly ProviderRecoveryCoordinator _owner;
    private int _reported;

    internal ProviderRecoveryLease(
        ProviderRecoveryCoordinator owner,
        ProviderRecoveryScope scope,
        Guid incidentId,
        long generation,
        bool isProbe,
        Action<ProviderRecoveryObservation>? observe)
    {
        _owner = owner;
        Scope = scope;
        IncidentId = incidentId;
        Generation = generation;
        IsProbe = isProbe;
        Observe = observe;
    }

    internal ProviderRecoveryScope Scope { get; }
    internal Guid IncidentId { get; }
    internal long Generation { get; }
    internal Action<ProviderRecoveryObservation>? Observe { get; }

    /// <summary>True only for the single call admitted while the circuit is half-open.</summary>
    public bool IsProbe { get; }

    /// <summary>Reports a successful call; only a current-generation probe may close a circuit.</summary>
    public void ReportSuccess() => Report(LeaseOutcome.Success, null);

    /// <summary>Reports a transient call failure and optional capped provider recovery floor.</summary>
    public void ReportTransientFailure(TimeSpan? retryAfter) => Report(LeaseOutcome.Transient, retryAfter);

    /// <summary>Releases admission without affecting transient circuit health.</summary>
    public void ReportNonTransientFailure() => Report(LeaseOutcome.NonTransient, null);

    private void Report(LeaseOutcome outcome, TimeSpan? retryAfter)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 0)
        {
            _owner.Report(this, outcome, retryAfter);
        }
    }

    internal enum LeaseOutcome
    {
        Success,
        Transient,
        NonTransient,
    }
}

/// <summary>Thread-safe in-memory provider recovery coordinator intended for singleton registration.</summary>
public sealed class ProviderRecoveryCoordinator : IProviderRecoveryCoordinator
{
    private readonly ProviderRecoveryOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<ProviderRecoveryObservation>? _observe;
    private readonly object _sync = new();
    private readonly Dictionary<ProviderRecoveryScope, ScopeState> _scopes = new(ScopeComparer.Instance);

    /// <summary>Creates a coordinator using system time and cancellable wall-clock delays.</summary>
    public ProviderRecoveryCoordinator(ProviderRecoveryOptions? options = null)
        : this(options ?? new ProviderRecoveryOptions(), () => DateTimeOffset.UtcNow, Task.Delay, null)
    {
    }

    /// <summary>Creates a coordinator with deterministic time seams for concurrency tests.</summary>
    /// <param name="options">Bounded recovery policy.</param>
    /// <param name="clock">Current-time source.</param>
    /// <param name="delay">Cancellable delay used for cooldown and admission deadlines.</param>
    public ProviderRecoveryCoordinator(
        ProviderRecoveryOptions options,
        Func<DateTimeOffset> clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<ProviderRecoveryObservation>? observe = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _observe = observe;
        if (_options.FailureThreshold <= 0 || _options.MaxQueueLength < 0 || _options.MaxConcurrentCalls <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Provider recovery bounds must be positive (queue may be zero).");
        }
    }

    /// <inheritdoc />
    public ValueTask<ProviderRecoveryLease> AcquireAsync(
        ProviderRecoveryScope scope,
        Guid incidentId,
        TimeSpan maxWait,
        CancellationToken cancellationToken,
        Action<ProviderRecoveryObservation>? observe = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxWait <= TimeSpan.Zero)
        {
            return ValueTask.FromException<ProviderRecoveryLease>(
                new ProviderRecoveryDeadlineExceededException(scope.Provider));
        }

        Waiter? waiter;
        lock (_sync)
        {
            var state = GetOrCreate(scope);
            PruneFailures(state, _clock());
            var immediate = TryAdmit(scope, state, incidentId, _clock(), observe);
            if (immediate is not null)
            {
                return ValueTask.FromResult(immediate);
            }

            if (state.Waiters.Count >= _options.MaxQueueLength)
            {
                return ValueTask.FromException<ProviderRecoveryLease>(
                    new ProviderRecoveryQueueFullException(scope.Provider));
            }

            waiter = new Waiter(scope, incidentId, _clock(), observe);
            state.Waiters.Enqueue(waiter);
            Notify(scope, state, ProviderRecoveryStage.AdmissionQueued, observe);
        }

        _ = CompleteWaitAsync(waiter, maxWait, cancellationToken);
        return new ValueTask<ProviderRecoveryLease>(waiter.Completion.Task);
    }

    /// <inheritdoc />
    public ProviderRecoverySnapshot GetSnapshot(ProviderRecoveryScope scope)
    {
        lock (_sync)
        {
            if (!_scopes.TryGetValue(scope, out var state))
            {
                return new ProviderRecoverySnapshot(ProviderRecoveryState.Closed, 0, 0, 0, 0, null);
            }

            PruneFailures(state, _clock());
            return new ProviderRecoverySnapshot(
                state.State,
                state.Generation,
                state.Failures.Count,
                state.InFlight,
                state.Waiters.Count(static waiter => !waiter.IsCompleted),
                state.State == ProviderRecoveryState.Open ? state.NextProbeAt : null);
        }
    }

    internal void Report(
        ProviderRecoveryLease lease,
        ProviderRecoveryLease.LeaseOutcome outcome,
        TimeSpan? retryAfter)
    {
        List<(Waiter Waiter, ProviderRecoveryLease Lease)> releases;
        lock (_sync)
        {
            var state = GetOrCreate(lease.Scope);
            state.InFlight = Math.Max(0, state.InFlight - 1);
            var now = _clock();
            PruneFailures(state, now);

            if (outcome == ProviderRecoveryLease.LeaseOutcome.Transient)
            {
                if (lease.IsProbe && lease.Generation == state.Generation && state.State == ProviderRecoveryState.HalfOpen)
                {
                    Open(lease.Scope, state, now, retryAfter, lease.Observe);
                }
                else if (state.State == ProviderRecoveryState.Closed)
                {
                    if (state.Failures.All(failure => failure.IncidentId != lease.IncidentId))
                    {
                        state.Failures.Add(new Failure(lease.IncidentId, now));
                    }

                    if (state.Failures.Count >= _options.FailureThreshold)
                    {
                        Open(lease.Scope, state, now, retryAfter, lease.Observe);
                    }
                }
            }
            else if (outcome == ProviderRecoveryLease.LeaseOutcome.Success
                     && lease.IsProbe
                     && lease.Generation == state.Generation
                     && state.State == ProviderRecoveryState.HalfOpen)
            {
                state.State = ProviderRecoveryState.Closed;
                state.Generation++;
                state.Failures.Clear();
                state.ReopenCount = 0;
                Notify(lease.Scope, state, ProviderRecoveryStage.Recovered, lease.Observe);
            }

            releases = DrainWaiters(lease.Scope, state, now);
        }

        CompleteReleases(releases);
    }

    private async Task CompleteWaitAsync(Waiter waiter, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        var deadlineAt = _clock() + maxWait;
        while (!waiter.IsCompleted)
        {
            var now = _clock();
            var remaining = deadlineAt - now;
            if (remaining <= TimeSpan.Zero)
            {
                RemoveAndFail(waiter, new ProviderRecoveryDeadlineExceededException(waiter.Scope.Provider));
                return;
            }

            DateTimeOffset? probeAt;
            lock (_sync)
            {
                var state = GetOrCreate(waiter.Scope);
                probeAt = state.State == ProviderRecoveryState.Open ? state.NextProbeAt : null;
            }

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var deadlineTask = _delay(remaining, wake.Token);
            var probeTask = probeAt is { } due && due < deadlineAt
                ? _delay(due > now ? due - now : TimeSpan.Zero, wake.Token)
                : Task.Delay(Timeout.InfiniteTimeSpan, wake.Token);

            try
            {
                var completed = await Task.WhenAny(waiter.Completion.Task, deadlineTask, probeTask).ConfigureAwait(false);
                if (completed == waiter.Completion.Task)
                {
                    wake.Cancel();
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    RemoveAndFail(waiter, new ProviderRecoveryCancelledException(waiter.Scope.Provider, cancellationToken));
                    return;
                }

                if (completed == deadlineTask)
                {
                    RemoveAndFail(waiter, new ProviderRecoveryDeadlineExceededException(waiter.Scope.Provider));
                    return;
                }

                List<(Waiter Waiter, ProviderRecoveryLease Lease)> releases;
                lock (_sync)
                {
                    var state = GetOrCreate(waiter.Scope);
                    releases = DrainWaiters(waiter.Scope, state, _clock());
                }

                CompleteReleases(releases);
            }
            catch (OperationCanceledException) when (waiter.IsCompleted)
            {
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RemoveAndFail(waiter, new ProviderRecoveryCancelledException(waiter.Scope.Provider, cancellationToken));
                return;
            }
        }
    }

    private void RemoveAndFail(Waiter waiter, Exception exception)
    {
        lock (_sync)
        {
            waiter.TrySetException(exception);
        }
    }

    private ProviderRecoveryLease? TryAdmit(
        ProviderRecoveryScope scope,
        ScopeState state,
        Guid incidentId,
        DateTimeOffset now,
        Action<ProviderRecoveryObservation>? observe)
    {
        if (state.State == ProviderRecoveryState.Open && now >= state.NextProbeAt)
        {
            // Calls admitted by an older closed generation may still be in flight. They must not
            // prevent the current generation's single half-open probe, otherwise a stale call can
            // indefinitely postpone recovery. The state transition itself is the single-flight
            // fence: after this admission the circuit is HalfOpen, so no second probe can enter.
            state.State = ProviderRecoveryState.HalfOpen;
            state.InFlight++;
            Notify(scope, state, ProviderRecoveryStage.ProbeAdmitted, observe);
            return new ProviderRecoveryLease(this, scope, incidentId, state.Generation, isProbe: true, observe);
        }

        if (state.State == ProviderRecoveryState.Closed && state.InFlight < _options.MaxConcurrentCalls)
        {
            state.InFlight++;
            return new ProviderRecoveryLease(this, scope, incidentId, state.Generation, isProbe: false, observe);
        }

        return null;
    }

    private List<(Waiter Waiter, ProviderRecoveryLease Lease)> DrainWaiters(
        ProviderRecoveryScope scope,
        ScopeState state,
        DateTimeOffset now)
    {
        var releases = new List<(Waiter, ProviderRecoveryLease)>();
        while (state.Waiters.Count > 0)
        {
            var waiter = state.Waiters.Peek();
            if (waiter.IsCompleted)
            {
                state.Waiters.Dequeue();
                continue;
            }

            var lease = TryAdmit(scope, state, waiter.IncidentId, now, waiter.Observe);
            if (lease is null)
            {
                break;
            }

            state.Waiters.Dequeue();
            releases.Add((waiter, lease));
            if (lease.IsProbe)
            {
                break;
            }
        }

        return releases;
    }

    private static void CompleteReleases(List<(Waiter Waiter, ProviderRecoveryLease Lease)> releases)
    {
        foreach (var (waiter, lease) in releases)
        {
            waiter.TrySetResult(lease);
        }
    }

    private void Open(
        ProviderRecoveryScope scope,
        ScopeState state,
        DateTimeOffset now,
        TimeSpan? retryAfter,
        Action<ProviderRecoveryObservation>? observe)
    {
        state.State = ProviderRecoveryState.Open;
        state.Generation++;
        state.ReopenCount++;
        var multiplier = Math.Pow(2, Math.Min(state.ReopenCount - 1, 10));
        var local = TimeSpan.FromMilliseconds(_options.EffectiveOpenDuration.TotalMilliseconds * multiplier);
        var floor = retryAfter is { } guidance && guidance > local ? guidance : local;
        var bounded = floor > _options.EffectiveMaxOpenDuration ? _options.EffectiveMaxOpenDuration : floor;
        state.NextProbeAt = now + bounded;
        Notify(scope, state, ProviderRecoveryStage.CircuitOpened, observe);
    }

    private void Notify(
        ProviderRecoveryScope scope,
        ScopeState state,
        ProviderRecoveryStage stage,
        Action<ProviderRecoveryObservation>? requestObserver)
    {
        if (_observe is null && requestObserver is null)
        {
            return;
        }

        var observation = new ProviderRecoveryObservation(
            stage,
            scope.Provider,
            state.State,
            state.Generation,
            state.InFlight,
            state.Waiters.Count(static waiter => !waiter.IsCompleted),
            state.State == ProviderRecoveryState.Open ? state.NextProbeAt : null);
        _observe?.Invoke(observation);
        if (!ReferenceEquals(_observe, requestObserver))
        {
            requestObserver?.Invoke(observation);
        }
    }

    private void PruneFailures(ScopeState state, DateTimeOffset now)
        => state.Failures.RemoveAll(failure => now - failure.At > _options.EffectiveFailureWindow);

    private ScopeState GetOrCreate(ProviderRecoveryScope scope)
    {
        var normalized = new ProviderRecoveryScope(scope.Provider, scope.AuthProfile ?? string.Empty);
        if (!_scopes.TryGetValue(normalized, out var state))
        {
            state = new ScopeState();
            _scopes.Add(normalized, state);
        }

        return state;
    }

    private sealed class ScopeState
    {
        public ProviderRecoveryState State { get; set; }
        public long Generation { get; set; }
        public int InFlight { get; set; }
        public int ReopenCount { get; set; }
        public DateTimeOffset NextProbeAt { get; set; }
        public List<Failure> Failures { get; } = [];
        public Queue<Waiter> Waiters { get; } = new();
    }

    private sealed class Waiter
    {
        private int _completed;

        public Waiter(
            ProviderRecoveryScope scope,
            Guid incidentId,
            DateTimeOffset enqueuedAt,
            Action<ProviderRecoveryObservation>? observe)
        {
            Scope = scope;
            IncidentId = incidentId;
            EnqueuedAt = enqueuedAt;
            Observe = observe;
        }

        public ProviderRecoveryScope Scope { get; }
        public Guid IncidentId { get; }
        public DateTimeOffset EnqueuedAt { get; }
        public Action<ProviderRecoveryObservation>? Observe { get; }
        public TaskCompletionSource<ProviderRecoveryLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsCompleted => Volatile.Read(ref _completed) != 0;

        public void TrySetResult(ProviderRecoveryLease lease)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                Completion.TrySetResult(lease);
            }
        }

        public void TrySetException(Exception exception)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                Completion.TrySetException(exception);
            }
        }
    }

    private readonly record struct Failure(Guid IncidentId, DateTimeOffset At);

    private sealed class ScopeComparer : IEqualityComparer<ProviderRecoveryScope>
    {
        public static readonly ScopeComparer Instance = new();

        public bool Equals(ProviderRecoveryScope x, ProviderRecoveryScope y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Provider, y.Provider)
               && StringComparer.Ordinal.Equals(x.AuthProfile, y.AuthProfile);

        public int GetHashCode(ProviderRecoveryScope obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Provider),
                StringComparer.Ordinal.GetHashCode(obj.AuthProfile ?? string.Empty));
    }
}
