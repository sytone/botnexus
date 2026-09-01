using System.IO.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Regression cover for #3738: the cross-process config lock's acquisition bound must be measured
/// against a MONOTONIC clock, so a host wall-clock step cannot extend or shorten it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> <see cref="CrossProcessConfigLock.AcquireAsync"/> used to compute an absolute
/// instant, <c>DateTime.UtcNow.AddMilliseconds(timeoutMs)</c>, and compare <c>DateTime.UtcNow</c>
/// against it on every retry. The wall clock is not monotonic - an NTP correction, a VM resume, or a
/// container host time sync can step it. A BACKWARDS step moves "now" away from the stored deadline,
/// so a bounded acquire that promises to fail after <c>timeoutMs</c> instead retries forever and the
/// config write path hangs. Since this lock guards every <c>PlatformConfigWriter</c> write, that is a
/// hang in the write path, not merely a slow one.
/// </para>
/// <para>
/// <b>Why these tests fail against the old implementation.</b> <see cref="HostileClock"/> reports
/// elapsed monotonic time that advances normally while its wall clock marches BACKWARDS on every
/// read. The headline test asserts the acquire still gives up. The old code consulted
/// <c>DateTime.UtcNow</c> directly - it had no clock seam at all - so it could not observe the
/// monotonic source, and under a receding wall clock its deadline comparison
/// (<c>UtcNow &gt;= deadline</c>) is never satisfied: the loop spins until the test host kills it.
/// <see cref="HostileClock.WallClockNeverReachedTheDeadline"/> asserts exactly that counterfactual
/// rather than leaving it as a claim in a comment, so the test cannot pass for the wrong reason.
/// </para>
/// <para>
/// <b>Non-vacuity.</b> A test that never contends the lock would take the very first success path and
/// prove nothing about any bound. Each test that asserts a timeout holds the sidecar open with
/// <see cref="FileShare.None"/> for the whole call, and additionally asserts the retry loop actually
/// iterated (<see cref="HostileClock.ElapsedReads"/> &gt; 1). Without those two guards the suite would
/// be satisfied by an implementation that returns immediately and never measures anything.
/// </para>
/// </remarks>
public sealed class CrossProcessConfigLockMonotonicBoundTests : IDisposable
{
    private const int TimeoutMs = 200;

    private readonly string _directory;
    private readonly string _configPath;

    public CrossProcessConfigLockMonotonicBoundTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"bn-3738-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle must not fail an otherwise-passing test.
        }
    }

    /// <summary>
    /// The headline property: with the wall clock stepping backwards throughout, a contended acquire
    /// still fails within its declared bound because the bound is monotonic.
    /// </summary>
    [Fact]
    public async Task ContendedAcquire_StillTimesOut_WhenTheWallClockStepsBackwards()
    {
        var fileSystem = new FileSystem();
        using var holder = OpenSidecarExclusively(fileSystem);

        // Monotonic time advances 40ms per read; the wall clock RECEDES an hour per read.
        var clock = new HostileClock(
            monotonicAdvance: TimeSpan.FromMilliseconds(40),
            wallClockStep: TimeSpan.FromHours(-1));

        var ex = await Should.ThrowAsync<PlatformConfigLockTimeoutException>(
            () => CrossProcessConfigLock.AcquireAsync(
                _configPath, fileSystem, CancellationToken.None, clock, TimeoutMs));

        // AC2: the declared timeout is still reported unchanged.
        ex.TimeoutMilliseconds.ShouldBe(TimeoutMs);
        ex.ConfigPath.ShouldBe(_configPath);

        // Non-vacuity: the loop genuinely retried rather than failing on the first attempt, and the
        // bound it honoured was the monotonic one.
        clock.ElapsedReads.ShouldBeGreaterThan(1);
        clock.MonotonicElapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(TimeoutMs));

        // The counterfactual that makes this a regression test: a UtcNow-derived deadline would NOT
        // have expired here, so the pre-#3738 implementation cannot pass this test.
        clock.WallClockNeverReachedTheDeadline(TimeSpan.FromMilliseconds(TimeoutMs)).ShouldBeTrue();
    }

    /// <summary>
    /// The mirror hazard: a FORWARDS wall-clock step must not expire a wait whose real elapsed time is
    /// still well inside the budget. Under the old implementation this raised a spurious timeout on a
    /// lock that had barely been contended.
    /// </summary>
    [Fact]
    public async Task Acquire_DoesNotExpireEarly_WhenTheWallClockJumpsForward()
    {
        var fileSystem = new FileSystem();
        using var holder = OpenSidecarExclusively(fileSystem);

        // Monotonic time barely moves; the wall clock leaps a day forward on every read.
        var clock = new HostileClock(
            monotonicAdvance: TimeSpan.FromMilliseconds(1),
            wallClockStep: TimeSpan.FromDays(1));

        using var cts = new CancellationTokenSource();
        var acquire = CrossProcessConfigLock.AcquireAsync(
            _configPath, fileSystem, cts.Token, clock, TimeoutMs);

        // Await a SIGNAL, not a fixed duration - the deterministic form the delay fence requires, and
        // the same discipline this issue is about. The clock raises this once the retry loop has
        // measured its elapsed time 12 times; at 1ms of monotonic advance per read that is ~12ms of
        // monotonic time against a 200ms bound, so a correct implementation CANNOT have given up yet.
        // A wall-clock implementation, by contrast, was 12 days past its deadline by the same point.
        await clock.WhenElapsedReadsReach(12);

        acquire.IsCompleted.ShouldBeFalse(
            "a forward wall-clock step must not expire a monotonic bound early");

        // Non-vacuity: the retry loop was running and measuring throughout.
        clock.ElapsedReads.ShouldBeGreaterThan(1);
        clock.MonotonicElapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(TimeoutMs));

        // The counterfactual: the wall clock is far past where a UtcNow deadline would have sat, so a
        // pre-#3738 implementation would already have thrown and failed the assertion above.
        clock.WallClockAdvancedBeyond(TimeSpan.FromMilliseconds(TimeoutMs)).ShouldBeTrue();

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => acquire);
    }

    /// <summary>
    /// AC5: with no clock adjustment at all, an uncontended acquire still succeeds and yields a
    /// disposable lock. The monotonic change must not alter the happy path.
    /// </summary>
    [Fact]
    public async Task UncontendedAcquire_StillSucceeds_UnderTheRealClock()
    {
        var fileSystem = new FileSystem();

        using var acquired = await CrossProcessConfigLock.AcquireAsync(
            _configPath, fileSystem, CancellationToken.None);

        acquired.ShouldNotBeNull();
    }

    private FileSystemStream OpenSidecarExclusively(IFileSystem fileSystem)
    {
        var lockPath = CrossProcessConfigLock.ResolveLockPath(_configPath, fileSystem);
        var directory = fileSystem.Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(directory))
            fileSystem.Directory.CreateDirectory(directory);

        return fileSystem.FileStream.New(
            lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose monotonic timestamp and whose wall clock disagree, modelling a
    /// host clock correction that occurs while a bounded wait is in progress.
    /// </summary>
    /// <remarks>
    /// Both surfaces advance on READ rather than on a schedule, which makes the test deterministic: the
    /// number of retry iterations, not the speed of the machine, decides when the bound is reached. That
    /// matters because the production loop uses a real <see cref="Task.Delay(int)"/> for its backoff and
    /// a wall-clock-driven fake would reintroduce the timing flakiness this issue is about.
    /// </remarks>
    private sealed class HostileClock(TimeSpan monotonicAdvance, TimeSpan wallClockStep) : TimeProvider
    {
        private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private long _monotonicTicks;
        private DateTimeOffset _wallClock = Origin;
        private int _elapsedReads;
        private int _readsAwaited = int.MaxValue;
        private readonly TaskCompletionSource _readsReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completes once the bounded wait has measured its elapsed time <paramref name="reads"/> times.
        /// </summary>
        /// <remarks>
        /// The synchronisation primitive that lets a test observe "the loop is still retrying" without
        /// sleeping for a wall-clock duration. Progress is counted in retry ITERATIONS, which the test
        /// controls, rather than in elapsed real time, which the machine controls - so the assertion is
        /// deterministic on a loaded CI runner.
        /// </remarks>
        public Task WhenElapsedReadsReach(int reads)
        {
            Volatile.Write(ref _readsAwaited, reads);
            if (ElapsedReads >= reads)
                _readsReached.TrySetResult();
            return _readsReached.Task;
        }

        /// <summary>Gets how many times the bounded wait measured its elapsed time.</summary>
        public int ElapsedReads => Volatile.Read(ref _elapsedReads);

        /// <summary>Gets the total monotonic time this clock has reported.</summary>
        public TimeSpan MonotonicElapsed => TimeSpan.FromTicks(Interlocked.Read(ref _monotonicTicks));

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            _wallClock = _wallClock.Add(wallClockStep);
            return _wallClock;
        }

        /// <remarks>
        /// The wall clock is stepped from here as well as from <see cref="GetUtcNow"/>. That is
        /// deliberate and load-bearing: the FIXED implementation never calls <c>GetUtcNow</c> at all, so
        /// a wall clock that only moved on that call would sit frozen at its origin and the
        /// counterfactual assertions would be vacuously false. Stepping on every clock read models the
        /// host correction happening DURING the wait, which is the scenario under test.
        /// </remarks>
        public override long GetTimestamp()
        {
            _wallClock = _wallClock.Add(wallClockStep);
            var reads = Interlocked.Increment(ref _elapsedReads);
            if (reads >= Volatile.Read(ref _readsAwaited))
                _readsReached.TrySetResult();
            return Interlocked.Add(ref _monotonicTicks, monotonicAdvance.Ticks);
        }

        /// <summary>
        /// Reports whether the wall clock has moved further from its origin than <paramref name="budget"/>,
        /// i.e. whether a <c>UtcNow &gt;= start + budget</c> deadline check would already have fired.
        /// </summary>
        public bool WallClockAdvancedBeyond(TimeSpan budget) => _wallClock >= Origin + budget;

        /// <summary>
        /// Reports whether the wall clock, over this run, ever advanced far enough past its starting
        /// instant to satisfy a <c>UtcNow &gt;= start + budget</c> deadline check.
        /// </summary>
        /// <remarks>
        /// This is the counterfactual guard. When it returns <see langword="true"/>, an implementation
        /// that measured its bound against the wall clock would still be waiting - so a passing timeout
        /// assertion can only have come from a monotonic measurement.
        /// </remarks>
        public bool WallClockNeverReachedTheDeadline(TimeSpan budget) => _wallClock < Origin + budget;
    }
}
