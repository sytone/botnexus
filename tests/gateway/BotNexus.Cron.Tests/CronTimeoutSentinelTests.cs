using System.Text.Json;
using BotNexus.Cron.Actions;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Covers #2904: <c>timeoutSeconds: 0</c> is an explicit "unlimited" sentinel, distinct from unset,
/// honoured identically by the scheduler and the command action, with invalid values warning rather
/// than being silently discarded.
/// </summary>
/// <remarks>
/// The value of these tests is the DISTINCTION, not the arithmetic. Before this change 0, -1 and
/// "banana" were all indistinguishable from an absent key: every one of them silently produced the
/// default. So each case here must assert something that separates it from that single old
/// behaviour - the unlimited cases assert a run OUTLIVES a default that would have killed it, and
/// the invalid cases assert a warning naming the value, not merely that the default came back.
/// </remarks>
public sealed class CronTimeoutSentinelTests
{
    private static CronJob JobWithTimeout(object? rawTimeout, string id = "job-timeout")
        => new()
        {
            Id = JobId.From(id),
            Name = "Timeout job",
            Schedule = "*/1 * * * *",
            ActionType = "test-action",
            Metadata = rawTimeout is null
                ? null
                : new Dictionary<string, object?> { ["timeoutSeconds"] = rawTimeout }
        };

    // ── AC1 + AC5: the sentinel resolves to unlimited across every accepted value shape ──────────

    public static TheoryData<object> ZeroShapes() =>
    [
        0,
        0L,
        0d,
        "0",
        JsonDocument.Parse("0").RootElement,
        JsonDocument.Parse("\"0\"").RootElement
    ];

    [Theory]
    [MemberData(nameof(ZeroShapes))]
    public void Resolve_ZeroInEveryValueShape_IsUnlimited(object rawZero)
    {
        var resolved = CronTimeoutResolver.Resolve(JobWithTimeout(rawZero), 120, NullLogger.Instance);

        // null IS the unlimited contract - a caller arms no CancelAfter for it.
        resolved.ShouldBeNull();
    }

    // ── AC5: positive values still resolve, in every shape ──────────────────────────────────────

    public static TheoryData<object> PositiveShapes() =>
    [
        7,
        7L,
        7.9d,
        "7",
        JsonDocument.Parse("7").RootElement,
        JsonDocument.Parse("\"7\"").RootElement
    ];

    [Theory]
    [MemberData(nameof(PositiveShapes))]
    public void Resolve_PositiveInEveryValueShape_IsHonoured(object rawPositive)
    {
        var resolved = CronTimeoutResolver.Resolve(JobWithTimeout(rawPositive), 120, NullLogger.Instance);

        resolved.ShouldBe(7);
    }

    // ── AC3: unset is unchanged ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_AbsentMetadata_ReturnsDefault()
    {
        CronTimeoutResolver.Resolve(JobWithTimeout(null), 120, NullLogger.Instance).ShouldBe(120);
    }

    [Fact]
    public void Resolve_MetadataWithoutTheKey_ReturnsDefault()
    {
        var job = JobWithTimeout(1) with
        {
            Metadata = new Dictionary<string, object?> { ["somethingElse"] = 99 }
        };

        CronTimeoutResolver.Resolve(job, 120, NullLogger.Instance).ShouldBe(120);
    }

    [Fact]
    public void Resolve_NullMetadataValue_ReturnsDefault()
    {
        var job = JobWithTimeout(1) with
        {
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = null }
        };

        CronTimeoutResolver.Resolve(job, 120, NullLogger.Instance).ShouldBe(120);
    }

    // ── AC4: invalid values warn, naming the job and the offending value ────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(-3600L)]
    public void Resolve_NegativeValue_ReturnsDefaultAndWarns(object rawNegative)
    {
        var logger = new CapturingLogger();

        var resolved = CronTimeoutResolver.Resolve(JobWithTimeout(rawNegative, "negative-job"), 120, logger);

        resolved.ShouldBe(120);
        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("negative-job");
        // The offending value must be named: a warning that omits it cannot be acted on.
        warning.ShouldContain(rawNegative.ToString()!, Case.Sensitive);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    public void Resolve_UnparseableValue_ReturnsDefaultAndWarns(string rawGarbage)
    {
        var logger = new CapturingLogger();

        var resolved = CronTimeoutResolver.Resolve(JobWithTimeout(rawGarbage, "garbage-job"), 120, logger);

        resolved.ShouldBe(120);
        logger.Warnings.ShouldHaveSingleItem().ShouldContain("garbage-job");
    }

    [Fact]
    public void Resolve_UnsupportedType_ReturnsDefaultAndWarns()
    {
        var logger = new CapturingLogger();

        var resolved = CronTimeoutResolver.Resolve(JobWithTimeout(true, "bool-job"), 120, logger);

        resolved.ShouldBe(120);
        logger.Warnings.ShouldHaveSingleItem().ShouldContain("bool-job");
    }

    [Fact]
    public void Resolve_ValidValues_DoNotWarn()
    {
        // Guards against the warning becoming the new silent-noise channel: only invalid input warns.
        var logger = new CapturingLogger();

        CronTimeoutResolver.Resolve(JobWithTimeout(0), 120, logger);
        CronTimeoutResolver.Resolve(JobWithTimeout(30), 120, logger);
        CronTimeoutResolver.Resolve(JobWithTimeout(null), 120, logger);

        logger.Warnings.ShouldBeEmpty();
    }

    // ── AC1/AC2 behavioural: the scheduler arms no timeout for the sentinel ─────────────────────

    [Fact]
    public async Task Scheduler_TimeoutSecondsZero_RunOutlivesTheDefaultAndSucceeds()
    {
        // Non-vacuity: the default is 1s and the action takes 3s, so if the sentinel were still
        // discarded (old behaviour) this run would record "timed_out". Only a genuinely unarmed
        // CancelAfter can produce "ok" here.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new DelayedAction("test-action", TimeSpan.FromSeconds(3));
        var job = CronStoreTestContext.CreateJob("job-unlimited", actionType: "test-action") with
        {
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = 0 }
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(
            context.Store,
            [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 1 });

        var run = await scheduler.RunNowAsync(JobId.From("job-unlimited"));

        run.Status.ShouldBe("ok");
        action.ExecutionCount.ShouldBe(1);
    }

    [Fact]
    public async Task Scheduler_TimeoutSecondsZero_StillCancelsPromptlyOnHostToken()
    {
        // AC2: unlimited removes the per-job cap, NOT the ambient token. A job that could not be
        // stopped by gateway shutdown would be a far worse defect than the one being fixed.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new AbortableAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-unlimited-abort", actionType: "test-action") with
        {
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = 0 }
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(
            context.Store,
            [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 });

        using var cts = new CancellationTokenSource();
        var runTask = scheduler.RunNowAsync(JobId.From("job-unlimited-abort"), cts.Token);
        await action.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await runTask.WaitAsync(TimeSpan.FromSeconds(10)));

        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-unlimited-abort"));
        history.ShouldHaveSingleItem().Status.ShouldBe("error");
    }

    // ── AC6 regression: the timeout-kill path is untouched for positive values ──────────────────

    [Fact]
    public async Task Scheduler_PositiveTimeout_StillTimesOut()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new DelayedAction("test-action", TimeSpan.FromSeconds(30));
        var job = CronStoreTestContext.CreateJob("job-positive", actionType: "test-action") with
        {
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = 1 }
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(
            context.Store,
            [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 });

        var run = await scheduler.RunNowAsync(JobId.From("job-positive"));

        run.Status.ShouldBe("timed_out");
    }

    [Fact]
    public async Task Scheduler_NegativeTimeout_FallsBackToDefaultAndTimesOut()
    {
        // -1 must NOT be read as "unlimited": it stays invalid and inherits the (short) default,
        // which the 30s action then exceeds.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new DelayedAction("test-action", TimeSpan.FromSeconds(30));
        var job = CronStoreTestContext.CreateJob("job-negative", actionType: "test-action") with
        {
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = -1 }
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(
            context.Store,
            [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 1 });

        var run = await scheduler.RunNowAsync(JobId.From("job-negative"));

        run.Status.ShouldBe("timed_out");
    }

    // ── CommandCronAction: the same sentinel, the same process path ─────────────────────────────

    [Fact]
    public async Task RunProcessAsync_UnlimitedTimeout_CompletesWithoutTimingOut()
    {
        var result = await CommandCronAction.RunProcessAsync(
            "Start-Sleep -Seconds 2; Write-Output 'survived'",
            timeoutSeconds: null,
            CancellationToken.None);

        result.TimedOut.ShouldBeFalse();
        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("survived");
    }

    [Fact]
    public async Task RunProcessAsync_UnlimitedTimeout_StillHonoursCancellationToken()
    {
        // The unlimited path must not become an uninterruptible process wait.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Should.ThrowAsync<OperationCanceledException>(
            () => CommandCronAction.RunProcessAsync(
                "Start-Sleep -Seconds 120",
                timeoutSeconds: null,
                cts.Token));
    }

    [Fact]
    public async Task CommandAction_TimeoutSecondsZero_DoesNotTimeOut()
    {
        var context = CommandContext(
            "Start-Sleep -Seconds 2",
            new Dictionary<string, object?> { ["timeoutSeconds"] = 0 });

        // No TimeoutException: the sentinel reached the process path as "no cap".
        await new CommandCronAction().ExecuteAsync(context);
    }

    [Fact]
    public async Task CommandAction_JsonElementZero_DoesNotTimeOut()
    {
        // Config-sourced metadata arrives as JsonElement; before #2904 this site did not even
        // recognise that shape, so a JSON 0 fell through to the 120s default.
        var context = CommandContext(
            "Start-Sleep -Seconds 2",
            new Dictionary<string, object?> { ["timeoutSeconds"] = JsonDocument.Parse("0").RootElement });

        await new CommandCronAction().ExecuteAsync(context);
    }

    [Fact]
    public async Task CommandAction_PositiveTimeout_StillTimesOut()
    {
        var context = CommandContext(
            "Start-Sleep -Seconds 60",
            new Dictionary<string, object?> { ["timeoutSeconds"] = 1 });

        var ex = await Should.ThrowAsync<TimeoutException>(() => new CommandCronAction().ExecuteAsync(context));
        ex.Message.ShouldContain("timed out after 1s");
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    private static CronExecutionContext CommandContext(
        string shellCommand,
        IReadOnlyDictionary<string, object?> metadata)
    {
        var job = new CronJob
        {
            Id = JobId.From("test-command-timeout-job"),
            Name = "Test Command",
            Schedule = "* * * * *",
            ActionType = "command",
            ShellCommand = shellCommand,
            Metadata = metadata
        };

        // #2462: firing passes an authorization gate that fails closed without a tool policy
        // provider, so the fixture supplies a permissive one - the same shape
        // CommandCronActionTests uses. These tests exercise timeout behaviour, not the gate.
        var provider = new ServiceCollection()
            .AddSingleton<BotNexus.Gateway.Abstractions.Security.IToolPolicyProvider>(
                new PermissiveToolPolicyProvider())
            .BuildServiceProvider();

        return new CronExecutionContext
        {
            Job = job,
            RunId = RunId.From(Guid.NewGuid().ToString()),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Manual,
            Services = provider
        };
    }

    /// <summary>Allows everything; used so these timeout-behaviour tests are not gated (#2462).</summary>
    private sealed class PermissiveToolPolicyProvider
        : BotNexus.Gateway.Abstractions.Security.IToolPolicyProvider
    {
        public BotNexus.Gateway.Abstractions.Security.ToolRiskLevel GetRiskLevel(string toolName)
            => BotNexus.Gateway.Abstractions.Security.ToolRiskLevel.Safe;

        public bool RequiresApproval(string toolName, string? agentId = null) => false;

        public BotNexus.Gateway.Abstractions.Security.ToolApprovalFallback GetApprovalFallback(
            string toolName, string? agentId = null)
            => BotNexus.Gateway.Abstractions.Security.ToolApprovalFallback.Allow;

        public IReadOnlyList<string> GetDeniedForHttp() => [];
    }

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        CronOptions options)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            actions,
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(options),
            NullLogger<CronScheduler>.Instance);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class DelayedAction(string actionType, TimeSpan delay) : ICronAction
    {
        private int _executionCount;
        public int ExecutionCount => _executionCount;
        public string ActionType => actionType;

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            Interlocked.Increment(ref _executionCount);
        }
    }

    private sealed class AbortableAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
