using BotNexus.Cron;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2671: a cron job's failure-alert target must be validated at scheduling time, by ONE shared
/// helper used by every authoring seam - while the fire-time guard stays exactly where it is.
/// </summary>
public sealed class CronAlertTargetValidationTests
{
    // Clause 3: alerting is opt-in. A null target is always valid.
    [Fact]
    public async Task Validate_NullTarget_IsValid_EvenWithNoResolver()
    {
        var result = await CronAlertTarget.ValidateAsync(resolver: null, conversationId: null);

        result.IsValid.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    // Clause 1/2 core: an unresolvable target is rejected and the error NAMES the id.
    [Fact]
    public async Task Validate_UnresolvableTarget_IsRejected_AndErrorNamesTheId()
    {
        var resolver = new StubResolver(known: []);

        var result = await CronAlertTarget.ValidateAsync(resolver, ConversationId.From("conv-typo"));

        result.IsValid.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("conv-typo");
    }

    [Fact]
    public async Task Validate_ResolvableTarget_IsValid()
    {
        var resolver = new StubResolver(known: ["conv-real"]);

        var result = await CronAlertTarget.ValidateAsync(resolver, ConversationId.From("conv-real"));

        result.IsValid.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    // Fails CLOSED, matching the #2462 command-authorization precedent: an unverifiable target
    // is refused rather than stored on the hope that it delivers.
    [Fact]
    public async Task Validate_TargetSupplied_ButNoResolver_FailsClosed()
    {
        var result = await CronAlertTarget.ValidateAsync(resolver: null, ConversationId.From("conv-x"));

        result.IsValid.ShouldBeFalse();
        result.Error!.ShouldContain("conv-x");
    }

    // Clause 5: a config-sourced job with a stale target WARNS naming the job id and keeps
    // loading. Refusing to boot the scheduler over one stale alert target would be a worse
    // failure than the one being fixed.
    [Fact]
    public async Task SyncConfiguredJobs_UnresolvableAlertTarget_WarnsNamingJobId_AndStillMaterialisesTheJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var logger = new ListLogger<CronScheduler>();
        var options = ConfigOptionsWithAlertTarget("cfg-stale-alert", "conv-gone");
        var scheduler = CreateScheduler(context.Store, options, logger, new StubResolver(known: []));

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        var stored = await context.Store.GetAsync(JobId.From("cfg-stale-alert"));
        stored.ShouldNotBeNull("a stale alert target must never stop a config job from loading");
        logger.Messages.ShouldContain(m =>
            m.Contains("cfg-stale-alert", StringComparison.Ordinal)
            && m.Contains("conv-gone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncConfiguredJobs_UnresolvableAlertTarget_DoesNotThrow()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var options = ConfigOptionsWithAlertTarget("cfg-noThrow", "conv-gone");
        var scheduler = CreateScheduler(
            context.Store, options, new ListLogger<CronScheduler>(), new StubResolver(known: []));

        await Should.NotThrowAsync(() => InvokeSyncConfiguredJobsAsync(scheduler, options));
    }

    [Fact]
    public async Task SyncConfiguredJobs_ResolvableAlertTarget_LoadsWithoutWarning()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var logger = new ListLogger<CronScheduler>();
        var options = ConfigOptionsWithAlertTarget("cfg-ok-alert", "conv-real");
        var scheduler = CreateScheduler(context.Store, options, logger, new StubResolver(known: ["conv-real"]));

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        var stored = await context.Store.GetAsync(JobId.From("cfg-ok-alert"));
        stored.ShouldNotBeNull();
        stored!.FailureAlertConversationId!.Value.Value.ShouldBe("conv-real");
        logger.Messages.ShouldNotContain(m => m.Contains("conv-real", StringComparison.Ordinal));
    }

    // Clause 4: create-time validation did NOT replace the fire-time guard. The conversation can
    // be deleted after the job is stored, so the :501 null check must still be reachable and
    // still be the thing that suppresses delivery.
    [Fact]
    public async Task FireTimeGuard_IsRetained_TargetClearedAfterStorage_SkipsDeliveryAndWarns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var logger = new ListLogger<CronScheduler>();
        var sink = new RecordingSink();

        // Stored WITH alerts on but no target - exactly the state authoring validation cannot
        // prevent, because the target was removed after the job was written.
        var job = CronStoreTestContext.CreateJob("job-fire-guard", actionType: "boom") with
        {
            FailureAlertsEnabled = true,
            FailureAlertConversationId = null
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateRunScheduler(context.Store, [new ThrowingAction("boom")], sink, logger);
        var run = await scheduler.RunNowAsync(JobId.From("job-fire-guard"));

        run.Status.ShouldBe(CronRunStatus.Error);
        sink.Count.ShouldBe(0);
        logger.Messages.ShouldContain(m =>
            m.Contains("job-fire-guard", StringComparison.Ordinal)
            && m.Contains("FailureAlertConversationId", StringComparison.Ordinal));
    }

    private static CronOptions ConfigOptionsWithAlertTarget(string jobId, string conversationId) => new()
    {
        Enabled = true,
        Jobs = new Dictionary<string, ConfiguredCronJob>
        {
            [jobId] = new()
            {
                Name = "Configured",
                Schedule = "*/5 * * * *",
                ActionType = "agent-prompt",
                AgentId = "agent-a",
                Message = "run",
                FailureAlertsEnabled = true,
                FailureAlertConversationId = conversationId
            }
        }
    };

    private static CronScheduler CreateScheduler(
        ICronStore store,
        CronOptions options,
        ILogger<CronScheduler> logger,
        ICronAlertTargetResolver? resolver)
    {
        var services = new ServiceCollection();
        if (resolver is not null)
            services.AddSingleton(resolver);
        var provider = services.BuildServiceProvider();
        return new CronScheduler(
            store,
            [new NoopAction("agent-prompt")],
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(options),
            logger);
    }

    private static CronScheduler CreateRunScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        ICronFailureAlertSink sink,
        ILogger<CronScheduler> logger)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sink);
        var provider = services.BuildServiceProvider();
        return new CronScheduler(
            store,
            actions,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions { Enabled = true, TickIntervalSeconds = 1 }),
            logger);
    }

    private static async Task InvokeSyncConfiguredJobsAsync(CronScheduler scheduler, CronOptions options)
    {
        var method = typeof(CronScheduler).GetMethod("SyncConfiguredJobsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [options, CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private sealed class StubResolver(IReadOnlyCollection<string> known) : ICronAlertTargetResolver
    {
        public Task<bool> ExistsAsync(ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult(known.Contains(conversationId.Value));
    }

    private sealed class RecordingSink : ICronFailureAlertSink
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("kaboom");
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) { return _messages.ToList(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages) { _messages.Add(formatter(state, exception)); }
        }
    }
}
