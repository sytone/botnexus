using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Issue #2748 clauses 3 and 4, asserted through <see cref="CronScheduler"/>'s OWN next-run
/// computation rather than through <c>CronTimeZoneResolver</c> directly.
/// <para>
/// This distinction is the whole point. <see cref="CronTimeZoneResolverTests"/> pins what the
/// canonical resolver does, but it cannot observe WHICH resolver the scheduling hot path calls.
/// The original defect was precisely that the scheduler had its own weaker spelling: every
/// resolver-level test stayed green while <c>expression.GetNextOccurrence(now, tz)</c> ran against
/// UTC and the job fired at the wrong hour. So these tests read the value the scheduler actually
/// persisted to <c>NextRunAt</c> - the only observable that proves which zone the tick used.
/// </para>
/// <para>
/// Clause 5: re-inlining a single-attempt <c>FindSystemTimeZoneById</c> resolver into
/// <see cref="CronScheduler"/> reddens
/// <see cref="WindowsAndIanaIds_ProduceTheSameNextRun_ThroughTheSchedulerTick"/> by name, because
/// the Windows-dialect job would then fall back to UTC and disagree with its IANA counterpart.
/// </para>
/// </summary>
public sealed class CronSchedulerTimeZoneResolutionTests
{
    private const string PacificWindowsId = "Pacific Standard Time";
    private const string PacificIanaId = "America/Los_Angeles";

    // Fires once a day at noon LOCAL time. A daily wall-clock schedule is the cheapest way to make
    // the resolved zone observable: the same expression yields a different UTC instant in every
    // zone, so a silent degradation to UTC changes NextRunAt rather than hiding inside it.
    private const string DailyAtNoon = "0 12 * * *";

    [Fact]
    public async Task WindowsAndIanaIds_ProduceTheSameNextRun_ThroughTheSchedulerTick()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        await CreateJobWithTimeZoneAsync(context, "job-windows", PacificWindowsId);
        await CreateJobWithTimeZoneAsync(context, "job-iana", PacificIanaId);
        await CreateJobWithTimeZoneAsync(context, "job-utc", "UTC");

        var scheduler = CreateScheduler(context.Store);
        await InvokeProcessTickAsync(scheduler);

        var windowsNext = await ReadNextRunAtAsync(context, "job-windows");
        var ianaNext = await ReadNextRunAtAsync(context, "job-iana");
        var utcNext = await ReadNextRunAtAsync(context, "job-utc");

        windowsNext.ShouldNotBeNull();
        ianaNext.ShouldNotBeNull();
        utcNext.ShouldNotBeNull();

        // The core invariant: the two spellings of ONE zone must schedule identically.
        windowsNext.ShouldBe(
            ianaNext,
            $"'{PacificWindowsId}' and '{PacificIanaId}' name the same zone, so the scheduler must " +
            "compute the same next run for both (#2748).");

        // Non-vacuity: if BOTH ids had silently degraded to UTC the equality above would still hold.
        // Pinning them apart from the genuinely-UTC job proves a real zone was resolved.
        windowsNext.ShouldNotBe(
            utcNext,
            "a Pacific-zoned daily schedule must not compute the same instant as a UTC-zoned one - " +
            "equal values here mean the timezone id degraded to UTC unnoticed.");
    }

    [Fact]
    public async Task UnresolvableId_DegradesToUtc_AndIsLoggedByTheSchedulerTick()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        await CreateJobWithTimeZoneAsync(context, "job-broken", "Not/ARealZone");
        await CreateJobWithTimeZoneAsync(context, "job-utc", "UTC");

        var logger = new CapturingLogger<CronScheduler>();
        var scheduler = CreateScheduler(context.Store, logger);
        await InvokeProcessTickAsync(scheduler);

        var brokenNext = await ReadNextRunAtAsync(context, "job-broken");
        var utcNext = await ReadNextRunAtAsync(context, "job-utc");

        // Fail-safe direction is preserved: an unresolvable id must not throw out of the tick.
        brokenNext.ShouldNotBeNull();
        brokenNext.ShouldBe(utcNext, "an unresolvable timezone id must still degrade to UTC, not throw.");

        // ...but it must no longer do so SILENTLY. Pre-#2748 this was swallowed entirely, which is
        // why a mis-scheduled job was undiagnosable from the logs.
        var degradation = logger.Entries.FirstOrDefault(entry =>
            entry.Message.Contains("Not/ARealZone", StringComparison.Ordinal));

        degradation.ShouldNotBeNull(
            "the degradation to UTC must be logged so a future occurrence is diagnosable. " +
            $"Captured entries: {string.Join(" | ", logger.Entries.Select(e => e.Message))}");
        degradation!.Level.ShouldBe(
            LogLevel.Warning,
            "a job silently running at the wrong hour is a warning-grade condition, not debug noise.");
    }

    [Fact]
    public async Task ResolvableId_LogsNoDegradationWarning()
    {
        // Guards the clause-4 assertion against becoming vacuous: if the resolver warned on every
        // id, the test above would pass even with resolution completely broken.
        await using var context = await CronStoreTestContext.CreateAsync();
        await CreateJobWithTimeZoneAsync(context, "job-iana", PacificIanaId);

        var logger = new CapturingLogger<CronScheduler>();
        var scheduler = CreateScheduler(context.Store, logger);
        await InvokeProcessTickAsync(scheduler);

        logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .Where(entry => entry.Message.Contains(PacificIanaId, StringComparison.Ordinal))
            .ShouldBeEmpty("a perfectly resolvable timezone id must not be reported as degraded.");
    }

    private static async Task CreateJobWithTimeZoneAsync(
        CronStoreTestContext context,
        string id,
        string timeZone)
    {
        // NextRunAt stays null so the tick takes the "seed the next run" branch: it computes and
        // persists NextRunAt without executing the action, which is exactly the path under test.
        var job = CronStoreTestContext.CreateJob(id, actionType: "test-action") with
        {
            Schedule = DailyAtNoon,
            TimeZone = timeZone,
            NextRunAt = null
        };

        await context.Store.CreateAsync(job);
    }

    private static async Task<DateTimeOffset?> ReadNextRunAtAsync(CronStoreTestContext context, string id)
    {
        var job = await context.Store.GetAsync(BotNexus.Domain.Primitives.JobId.From(id));
        job.ShouldNotBeNull($"job '{id}' should exist");
        return job!.NextRunAt;
    }

    private static CronScheduler CreateScheduler(ICronStore store, ILogger<CronScheduler>? logger = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new CronScheduler(
            store,
            [],
            services.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions { Enabled = true, TickIntervalSeconds = 1 }),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CronScheduler>.Instance);
    }

    private static async Task InvokeProcessTickAsync(CronScheduler scheduler)
    {
        var method = typeof(CronScheduler).GetMethod("ProcessTickAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
