using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2985: an execution-class <c>agent-prompt</c> cron run that completes having made ZERO tool
/// invocations must not be recorded as <c>ok</c>.
///
/// <para>
/// The defect these tests pin is not a crash - it is the ABSENCE of a distinction. Four consecutive
/// runs of the autonomous-maintenance job on 2026-08-11 completed in 9-11 seconds, made no tool
/// calls, emitted fabricated reports, and each recorded <c>status: ok, error: null</c>: byte-identical
/// to the healthy 200-550 second runs of the same job. Nothing in run history, the portal, or the
/// alerting path could tell them apart. A test that only asserts "the scheduler still records ok on
/// success" would therefore have passed happily throughout the incident, which is why every clause
/// below asserts on the RECORDED OUTCOME rather than on the absence of an exception.
/// </para>
/// </summary>
public sealed class CronZeroToolCallOutcomeTests
{
    private const string AlertConversationId = "conv-2985-alerts";

    /// <summary>
    /// #2985 clause 1: an execution-class run with zero tool invocations records a non-ok status,
    /// and the recorded reason names the zero-tool-call condition.
    /// </summary>
    /// <remarks>
    /// This is the MUTATION TARGET named by clause 5. Reverting the zero-tool condition in
    /// <c>CronScheduler.DetectZeroToolCallOutcome</c> (returning null unconditionally, or dropping
    /// the <c>ExecutionClass</c>/count check) must redden THIS test by name.
    /// </remarks>
    [Fact]
    public async Task ExecutionClassRun_WithZeroToolCalls_RecordsNonOkStatusNamingTheCondition()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(ExecutionClassJob("job-zero"));

        // The action completes normally and reports a tool count of zero - exactly the shape of the
        // four fabricated 2026-08-11 runs: the turn returned, nothing threw, no tool ran.
        var scheduler = CreateScheduler(context.Store, [new ToolCountingAction("boom", toolInvocationCount: 0)]);

        var run = await scheduler.RunNowAsync(JobId.From("job-zero"));

        run.Status.ShouldNotBe(CronRunStatus.Ok, "a run that invoked no tools did no work and must not read as success");
        run.Status.ShouldBe(CronRunStatus.NoToolCalls);

        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-zero"));
        var entry = history.ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.NoToolCalls);
        entry.Error.ShouldNotBeNull("the recorded reason must name the condition, not be null");
        entry.Error!.ShouldContain("zero tool calls");
    }

    /// <summary>
    /// #2985 clause 2: the same run does not present as a success with a null error, and run
    /// history distinguishes it from a healthy run WITHOUT reading session_history. The incident
    /// was found only by hand-counting session rows days later; that is the gap being closed.
    /// </summary>
    [Fact]
    public async Task ExecutionClassZeroToolRun_IsDistinguishableFromHealthyRun_FromRunHistoryAlone()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(ExecutionClassJob("job-mixed"));

        // One healthy run (3 tools) then one do-nothing run (0 tools), same job, same action type.
        var working = new ToolCountingAction("boom", toolInvocationCount: 3);
        var idle = new ToolCountingAction("boom", toolInvocationCount: 0);

        await CreateScheduler(context.Store, [working]).RunNowAsync(JobId.From("job-mixed"));
        await CreateScheduler(context.Store, [idle]).RunNowAsync(JobId.From("job-mixed"));

        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-mixed"));
        history.Count.ShouldBe(2);

        // History is newest-first: the zero-tool run is the newest row.
        var zeroToolRun = history[0];
        var healthyRun = history[1];

        zeroToolRun.Status.ShouldNotBe(healthyRun.Status,
            "the two runs must be distinguishable from run history alone - that is the whole defect");
        zeroToolRun.Status.ShouldBe(CronRunStatus.NoToolCalls);
        zeroToolRun.Error.ShouldNotBeNull();
        healthyRun.Status.ShouldBe(CronRunStatus.Ok);
        healthyRun.Error.ShouldBeNull();

        // And the job's own lastRunStatus - the field the portal renders - carries the non-success.
        var job = await context.Store.GetAsync(JobId.From("job-mixed"));
        job!.LastRunStatus.ShouldBe(CronRunStatus.NoToolCalls);
        job.LastRunError.ShouldNotBeNull();
    }

    /// <summary>
    /// #2985 clause 3: a run that DOES invoke tools continues to record <c>ok</c> - no behaviour
    /// change for healthy runs. Asserted with an execution-class job so the marker being on is
    /// demonstrably not sufficient on its own to demote a run.
    /// </summary>
    [Fact]
    public async Task ExecutionClassRun_WithToolCalls_StillRecordsOk()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(ExecutionClassJob("job-working"));

        var scheduler = CreateScheduler(context.Store, [new ToolCountingAction("boom", toolInvocationCount: 1)]);

        var run = await scheduler.RunNowAsync(JobId.From("job-working"));

        run.Status.ShouldBe(CronRunStatus.Ok);
        run.Error.ShouldBeNull();

        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-working"))).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Ok);
        entry.Error.ShouldBeNull();

        var job = await context.Store.GetAsync(JobId.From("job-working"));
        job!.LastRunStatus.ShouldBe(CronRunStatus.Ok);
    }

    /// <summary>
    /// #2985 clause 4: a job NOT marked execution-class is unaffected and may complete with zero
    /// tool calls as <c>ok</c>. A reporting or classification job legitimately answers from context
    /// without calling a tool; demoting those would make the new signal worthless within a day.
    /// </summary>
    [Fact]
    public async Task NonExecutionClassRun_WithZeroToolCalls_StillRecordsOk()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-reporting", actionType: "boom");
        job.ExecutionClass.ShouldBeFalse("the execution-class marker must be OFF by default");
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [new ToolCountingAction("boom", toolInvocationCount: 0)]);

        var run = await scheduler.RunNowAsync(JobId.From("job-reporting"));

        run.Status.ShouldBe(CronRunStatus.Ok);
        run.Error.ShouldBeNull();

        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-reporting"))).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Ok);
        entry.Error.ShouldBeNull();
    }

    /// <summary>
    /// An action that reports NO tool count at all (command / webhook - they have no tool concept)
    /// must never be read as "zero tools", even when the job is marked execution-class. Null is not
    /// zero; collapsing the two would classify every shell job on the platform as a do-nothing run.
    /// </summary>
    [Fact]
    public async Task ExecutionClassRun_WhenActionReportsNoToolCount_StillRecordsOk()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(ExecutionClassJob("job-silent"));

        // SilentAction never calls RecordToolInvocationCount - ToolInvocationCount stays null.
        var scheduler = CreateScheduler(context.Store, [new SilentAction("boom")]);

        var run = await scheduler.RunNowAsync(JobId.From("job-silent"));

        run.Status.ShouldBe(CronRunStatus.Ok,
            "an action that reports no tool count has expressed no opinion; null must not be read as zero");
    }

    /// <summary>
    /// The zero-tool outcome drives the EXISTING failure-alert path (#2557) rather than a parallel
    /// notification channel. Without this the fix would be recorded-but-silent, which is only half
    /// the defect: the issue explicitly notes run status is the input to alerting.
    /// </summary>
    [Fact]
    public async Task ExecutionClassZeroToolRun_DeliversThroughExistingFailureAlertPath()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        await context.Store.CreateAsync(ExecutionClassJob("job-alert") with
        {
            FailureAlertsEnabled = true,
            FailureAlertConversationId = ConversationId.From(AlertConversationId)
        });

        var scheduler = CreateScheduler(context.Store, [new ToolCountingAction("boom", toolInvocationCount: 0)], sink);

        await scheduler.RunNowAsync(JobId.From("job-alert"));

        var captured = sink.Alerts.ShouldHaveSingleItem();
        captured.ConversationId.Value.ShouldBe(AlertConversationId);
        captured.Alert.JobId.Value.ShouldBe("job-alert");
        captured.Alert.Error.ShouldNotBeNull();
        captured.Alert.Error!.ShouldContain("zero tool calls");
    }

    /// <summary>
    /// Alerting opt-out is honoured for the zero-tool outcome exactly as it is for errors: a job
    /// without <c>FailureAlertsEnabled</c> delivers nothing. Guards against the new outcome
    /// accidentally bypassing the opt-in gate.
    /// </summary>
    [Fact]
    public async Task ExecutionClassZeroToolRun_WithAlertsDisabled_DeliversNothing()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        await context.Store.CreateAsync(ExecutionClassJob("job-noalert"));

        var scheduler = CreateScheduler(context.Store, [new ToolCountingAction("boom", toolInvocationCount: 0)], sink);

        await scheduler.RunNowAsync(JobId.From("job-noalert"));

        sink.Alerts.ShouldBeEmpty();
    }

    /// <summary>
    /// The execution-class marker survives a store round-trip. A marker that is accepted in memory
    /// but dropped on persistence would make every clause above pass in tests and fail in
    /// production on the very next gateway restart.
    /// </summary>
    [Fact]
    public async Task ExecutionClassMarker_RoundTripsThroughTheStore()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(ExecutionClassJob("job-persist"));

        var loaded = await context.Store.GetAsync(JobId.From("job-persist"));
        loaded!.ExecutionClass.ShouldBeTrue();

        var listed = (await context.Store.ListAsync()).ShouldHaveSingleItem();
        listed.ExecutionClass.ShouldBeTrue();

        // And an update that does not mention the marker must not clear it.
        await context.Store.UpdateDefinitionAsync(loaded with { Name = "renamed" });
        (await context.Store.GetAsync(JobId.From("job-persist")))!.ExecutionClass.ShouldBeTrue();
    }

    /// <summary>
    /// <c>no_tool_calls</c> is a TERMINAL status, so retention must be able to purge it. Omitting
    /// it from the purge filter would make those rows permanently immune to cleanup - the same
    /// unbounded-growth trap #2410 found for orphaned <c>running</c> rows.
    /// </summary>
    [Fact]
    public async Task NoToolCallsRuns_ArePurgeableByRetention()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(ExecutionClassJob("job-retain"));

        var run = await context.Store.RecordRunStartAsync(JobId.From("job-retain"));
        await context.Store.RecordRunCompleteAsync(run.Id, CronRunStatus.NoToolCalls, "no work");

        var purged = await context.Store.PurgeRunsOlderThanAsync(DateTimeOffset.UtcNow.AddYears(1));

        purged.ShouldBe(1);
        (await context.Store.GetRunHistoryAsync(JobId.From("job-retain"))).ShouldBeEmpty();
    }

    /// <summary>
    /// The status constant is part of the persisted contract (see <see cref="CronRunStatus"/>) and
    /// must be a distinct value, not an alias of an existing one.
    /// </summary>
    [Fact]
    public void NoToolCallsStatus_IsADistinctCanonicalValue()
    {
        CronRunStatus.NoToolCalls.ShouldBe("no_tool_calls");

        var all = new[]
        {
            CronRunStatus.Ok, CronRunStatus.Error, CronRunStatus.TimedOut,
            CronRunStatus.Running, CronRunStatus.Skipped, CronRunStatus.Missed,
            CronRunStatus.NoToolCalls
        };
        all.Distinct(StringComparer.Ordinal).Count().ShouldBe(all.Length);
    }

    // --- helpers ---

    private static CronJob ExecutionClassJob(string id)
        => CronStoreTestContext.CreateJob(id, actionType: "boom") with { ExecutionClass = true };

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        ICronFailureAlertSink? sink = null)
    {
        var services = new ServiceCollection();
        if (sink is not null)
            services.AddSingleton(sink);
        services.AddSingleton<ISecretRedactor>(new PassthroughRedactor());
        var provider = services.BuildServiceProvider();

        return new CronScheduler(
            store,
            actions,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 }),
            NullLogger<CronScheduler>.Instance);
    }

    /// <summary>
    /// An action that completes normally and reports a specific tool-invocation count, standing in
    /// for the agent-prompt action's report of its turn's tool timeline.
    /// </summary>
    private sealed class ToolCountingAction(string actionType, int toolInvocationCount) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            context.RecordToolInvocationCount(toolInvocationCount);
            return Task.CompletedTask;
        }
    }

    /// <summary>An action that reports no tool count at all (command / webhook shape).</summary>
    private sealed class SilentAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed record CapturedAlert(ConversationId ConversationId, CronFailureAlert Alert);

    private sealed class RecordingAlertSink : ICronFailureAlertSink
    {
        private readonly List<CapturedAlert> _alerts = [];

        public IReadOnlyList<CapturedAlert> Alerts
        {
            get { lock (_alerts) { return _alerts.ToList(); } }
        }

        public Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default)
        {
            lock (_alerts) { _alerts.Add(new CapturedAlert(conversationId, alert)); }
            return Task.CompletedTask;
        }
    }

    private sealed class PassthroughRedactor : ISecretRedactor
    {
        public string Redact(string input) => input;
        public string RedactForExternalDelivery(string input) => input;
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
