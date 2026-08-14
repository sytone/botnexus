using BotNexus.Cron.Actions;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #3161: a cron run whose <b>primary delivery</b> failed must not be recorded as a plain success.
///
/// <para>
/// The defect these tests pin is, like #2985, the ABSENCE of a distinction rather than a crash.
/// <c>RunActionAsync</c> derived the terminal status purely from whether the action threw; whether
/// the run's output actually reached the destination conversation was never consulted. A job whose
/// destination is archived, deleted, or otherwise unreachable therefore produced an unbroken streak
/// of green runs forever, and <c>CountConsecutiveErrorsAsync</c> could not help because no error was
/// ever recorded. Every clause below asserts on the RECORDED OUTCOME, not on the absence of an
/// exception, because a test written the other way would have passed throughout the whole failure.
/// </para>
/// </summary>
public sealed class CronDeliveryOutcomeTests
{
    private const string AlertConversationId = "conv-3161-alerts";

    /// <summary>
    /// #3161 AC1: primary delivery throws for a job whose agent turn otherwise succeeded; the
    /// recorded run status must not be <c>ok</c>.
    /// </summary>
    /// <remarks>
    /// MUTATION TARGET. Collapsing <c>ResolveTerminalOutcome</c> back to "delivery is ignored"
    /// (dropping the <c>context.DeliveryError</c> branch) must redden THIS test by name.
    /// </remarks>
    [Fact]
    public async Task SuccessfulTurn_WithFailedPrimaryDelivery_IsNotRecordedAsOk()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-3161-a", actionType: "boom"));

        // The action itself completes normally - only its delivery blows up. Pre-#3161 this run
        // recorded status ok / error null, byte-identical to a run that actually delivered.
        var scheduler = CreateScheduler(context.Store, [new FailingDeliveryAction("boom", "conversation is archived")]);

        var run = await scheduler.RunNowAsync(JobId.From("job-3161-a"));

        run.Status.ShouldNotBe(CronRunStatus.Ok,
            "a run whose output reached nobody did not succeed and must not read as success");
        run.Status.ShouldBe(CronRunStatus.DeliveryFailed);

        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-3161-a"))).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.DeliveryFailed);
        entry.Error.ShouldNotBeNull("the recorded reason must name the delivery failure, not be null");
        entry.Error!.ShouldContain("conversation is archived");

        // And the field the portal renders carries the non-success too.
        var job = await context.Store.GetAsync(JobId.From("job-3161-a"));
        job!.LastRunStatus.ShouldBe(CronRunStatus.DeliveryFailed);
        job.LastRunError.ShouldNotBeNull();
    }

    /// <summary>
    /// #3161 AC2: the same run delivers a failure alert. Recording the outcome without alerting
    /// would be only half the fix - the operator still learns nothing until they read run history.
    /// </summary>
    [Fact]
    public async Task FailedPrimaryDelivery_DeliversAFailureAlert()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        await context.Store.CreateAsync(AlertingJob("job-3161-b"));

        var scheduler = CreateScheduler(
            context.Store,
            [new FailingDeliveryAction("boom", "conversation is archived")],
            sink);

        await scheduler.RunNowAsync(JobId.From("job-3161-b"));

        var captured = sink.Alerts.ShouldHaveSingleItem();
        captured.ConversationId.Value.ShouldBe(AlertConversationId);
        captured.Alert.JobId.Value.ShouldBe("job-3161-b");
        captured.Alert.Error.ShouldNotBeNull();
        captured.Alert.Error!.ShouldContain("conversation is archived");
    }

    /// <summary>
    /// #3161 AC3: when NO alternate destination is configured, an unreachable alert destination is
    /// still recorded against the run rather than only logged. Pre-#3161 the sole trace of a
    /// swallowed alert was one Error log line, which nothing queries and no operator reads.
    /// </summary>
    /// <remarks>
    /// Fail-closed, not fail-loud: the run's own terminal status is deliberately NOT rewritten by
    /// an alert-delivery failure (that would violate the #2557 AC7 containment this issue's AC5
    /// requires preserving). What changes is that the failure becomes part of the run's recorded
    /// error, so it is discoverable from run history.
    /// </remarks>
    [Fact]
    public async Task UnreachableAlertDestination_WithNoAlternate_IsRecordedAgainstTheRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(AlertingJob("job-3161-c"));

        var scheduler = CreateScheduler(
            context.Store,
            [new FailingDeliveryAction("boom", "conversation is archived")],
            new ThrowingAlertSink("alert conversation does not exist"));

        var run = await scheduler.RunNowAsync(JobId.From("job-3161-c"));

        // Containment preserved: the alert failure did not become the run's outcome.
        run.Status.ShouldBe(CronRunStatus.DeliveryFailed);

        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-3161-c"))).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.DeliveryFailed);
        entry.Error.ShouldNotBeNull();
        entry.Error!.ShouldContain("conversation is archived");
        // An alert that could not be delivered must be visible in run history, not only in a log line.
        entry.Error.ShouldContain(CronScheduler.AlertDeliveryFailurePrefix);
        entry.Error.ShouldContain("alert conversation does not exist");

        var job = await context.Store.GetAsync(JobId.From("job-3161-c"));
        job!.LastRunError.ShouldNotBeNull();
        job.LastRunError!.ShouldContain(CronScheduler.AlertDeliveryFailurePrefix);
    }

    /// <summary>
    /// #3161 AC3 (companion): the same fail-closed recording applies when the run itself errored -
    /// the alert-containment path is shared, so it must not record for one outcome only.
    /// </summary>
    [Fact]
    public async Task UnreachableAlertDestination_OnAnErroredRun_IsAlsoRecordedAgainstTheRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(AlertingJob("job-3161-d"));

        var scheduler = CreateScheduler(
            context.Store,
            [new ThrowingAction("boom", "kaboom")],
            new ThrowingAlertSink("sink is down"));

        var run = await scheduler.RunNowAsync(JobId.From("job-3161-d"));

        run.Status.ShouldBe(CronRunStatus.Error);
        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-3161-d"))).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Error);
        entry.Error.ShouldNotBeNull();
        entry.Error!.ShouldContain("kaboom");
        entry.Error.ShouldContain(CronScheduler.AlertDeliveryFailurePrefix);
    }

    /// <summary>
    /// #3161 AC4: a delivery failure counts toward the consecutive-error streak. Proven over TWO
    /// consecutive delivery failures: if <c>delivery_failed</c> were not an alertable failure
    /// status the streak would restart at 1 every run, and the backoff would deliver an alert on
    /// every single run forever - the exact noise #2557's backoff exists to prevent.
    /// </summary>
    [Fact]
    public async Task TwoConsecutiveDeliveryFailures_AdvanceTheErrorStreak()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        await context.Store.CreateAsync(AlertingJob("job-3161-e"));

        var scheduler = CreateScheduler(
            context.Store,
            [new FailingDeliveryAction("boom", "conversation is archived")],
            sink);

        await scheduler.RunNowAsync(JobId.From("job-3161-e"));
        await scheduler.RunNowAsync(JobId.From("job-3161-e"));

        sink.Alerts.Count.ShouldBe(2);
        sink.Alerts.Select(a => a.Alert.ConsecutiveErrorCount).ShouldBe(new[] { 1, 2 },
            "the second delivery failure must read as streak position 2, not as a fresh streak");
    }

    /// <summary>
    /// #3161 AC4 (mixed streak): a delivery failure and an error belong to the SAME streak. Two
    /// different non-success outcomes in a row must not reset the backoff between them.
    /// </summary>
    [Fact]
    public async Task DeliveryFailureFollowedByError_ContinuesTheSameStreak()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        await context.Store.CreateAsync(AlertingJob("job-3161-f"));

        await CreateScheduler(context.Store, [new FailingDeliveryAction("boom", "gone")], sink)
            .RunNowAsync(JobId.From("job-3161-f"));
        await CreateScheduler(context.Store, [new ThrowingAction("boom", "kaboom")], sink)
            .RunNowAsync(JobId.From("job-3161-f"));

        sink.Alerts.Select(a => a.Alert.ConsecutiveErrorCount).ShouldBe(new[] { 1, 2 });
    }

    /// <summary>
    /// A successful delivery is unaffected: the run still records <c>ok</c> with a null error. The
    /// new outcome must be reachable only by an action that actually reported a delivery failure -
    /// otherwise the signal is worthless within a day (the #2985 lesson).
    /// </summary>
    [Fact]
    public async Task SuccessfulDelivery_StillRecordsOk()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-3161-g", actionType: "boom"));

        var scheduler = CreateScheduler(context.Store, [new SucceedingDeliveryAction("boom")]);

        var run = await scheduler.RunNowAsync(JobId.From("job-3161-g"));

        run.Status.ShouldBe(CronRunStatus.Ok);
        run.Error.ShouldBeNull();

        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-3161-g"))).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Ok);
        entry.Error.ShouldBeNull();
    }

    /// <summary>
    /// An action that never uses the delivery seam at all (command / webhook shape) is untouched.
    /// Silence must not be read as a delivery failure - the null-is-not-zero rule from #2985.
    /// </summary>
    [Fact]
    public async Task ActionThatNeverReportsDelivery_StillRecordsOk()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-3161-h", actionType: "boom"));

        var scheduler = CreateScheduler(context.Store, [new SilentAction("boom")]);

        (await scheduler.RunNowAsync(JobId.From("job-3161-h"))).Status.ShouldBe(CronRunStatus.Ok);
    }

    /// <summary>
    /// The delivery seam contains the exception rather than propagating it: a failed delivery is a
    /// recorded outcome, not a thrown one. If it escaped, the run would record <c>error</c> and the
    /// operator could not distinguish "the job broke" from "the job worked but reached nobody".
    /// </summary>
    [Fact]
    public async Task DeliverAsync_ContainsTheExceptionAndRecordsIt()
    {
        var context = new CronExecutionContext
        {
            Job = CronStoreTestContext.CreateJob("job-3161-seam"),
            RunId = RunId.From("run-1"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Manual,
            Services = new ServiceCollection().BuildServiceProvider()
        };

        context.DeliveryError.ShouldBeNull("a context that never delivered has expressed no opinion");

        await context.DeliverAsync(_ => throw new InvalidOperationException("target gone"));

        context.DeliveryError.ShouldNotBeNull();
        context.DeliveryError!.ShouldContain("target gone");
    }

    /// <summary>
    /// Host cancellation is NOT a delivery failure: it must propagate so the scheduler's abort path
    /// records the run as aborted. Swallowing it here would convert every gateway shutdown into a
    /// spurious delivery-failure alert storm.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PropagatesHostCancellation()
    {
        var context = new CronExecutionContext
        {
            Job = CronStoreTestContext.CreateJob("job-3161-cancel"),
            RunId = RunId.From("run-2"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Manual,
            Services = new ServiceCollection().BuildServiceProvider()
        };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => context.DeliverAsync(ct => Task.FromCanceled(ct), cts.Token));

        context.DeliveryError.ShouldBeNull("a cancelled host is not a delivery failure");
    }

    /// <summary>
    /// The status constant is part of the persisted contract (see <see cref="CronRunStatus"/>) and
    /// must be a distinct value, not an alias of an existing one.
    /// </summary>
    [Fact]
    public void DeliveryFailedStatus_IsADistinctCanonicalValue()
    {
        CronRunStatus.DeliveryFailed.ShouldBe("delivery_failed");

        var all = new[]
        {
            CronRunStatus.Ok, CronRunStatus.Error, CronRunStatus.TimedOut,
            CronRunStatus.Running, CronRunStatus.Skipped, CronRunStatus.Missed,
            CronRunStatus.NoToolCalls, CronRunStatus.DeliveryFailed
        };
        all.Distinct(StringComparer.Ordinal).Count().ShouldBe(all.Length);
    }

    /// <summary>
    /// <c>delivery_failed</c> is TERMINAL, so retention must be able to purge it. Omitting it from
    /// the purge filter would make those rows permanently immune to cleanup - the unbounded-growth
    /// trap #2410 found for orphaned <c>running</c> rows and #2985 re-checked for the new status.
    /// </summary>
    [Fact]
    public async Task DeliveryFailedRuns_ArePurgeableByRetention()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-3161-purge", actionType: "boom"));

        var run = await context.Store.RecordRunStartAsync(JobId.From("job-3161-purge"));
        await context.Store.RecordRunCompleteAsync(run.Id, CronRunStatus.DeliveryFailed, "undeliverable");

        (await context.Store.PurgeRunsOlderThanAsync(DateTimeOffset.UtcNow.AddYears(1))).ShouldBe(1);
        (await context.Store.GetRunHistoryAsync(JobId.From("job-3161-purge"))).ShouldBeEmpty();
    }

    /// <summary>
    /// #3161 wiring, end of the real chain: <c>AgentPromptAction</c> must forward a delivery failure
    /// the trigger reported into the execution context. Without this the whole mechanism is inert in
    /// production - the seam and the status would exist and nothing would ever set them, which is
    /// exactly the kind of half-fix that passes its own tests and changes nothing on the platform.
    /// </summary>
    [Fact]
    public async Task AgentPromptAction_ForwardsATriggerReportedDeliveryFailure()
    {
        var services = new ServiceCollection()
            .AddSingleton<IInternalTrigger>(new DeliveryFailingTrigger("destination conversation is gone"))
            .BuildServiceProvider();

        var context = new CronExecutionContext
        {
            Job = CronStoreTestContext.CreateJob("job-3161-wiring"),
            RunId = RunId.From("run-wiring"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Scheduled,
            Services = services
        };

        await new AgentPromptAction().ExecuteAsync(context);

        context.DeliveryError.ShouldBe("destination conversation is gone");
    }

    /// <summary>
    /// The converse of the wiring test: a trigger that reports no delivery problem leaves the
    /// context clean, so a healthy agent-prompt run still records <c>ok</c>.
    /// </summary>
    [Fact]
    public async Task AgentPromptAction_LeavesDeliveryErrorNull_WhenTheTriggerReportsNone()
    {
        var services = new ServiceCollection()
            .AddSingleton<IInternalTrigger>(new DeliveryFailingTrigger(deliveryError: null))
            .BuildServiceProvider();

        var context = new CronExecutionContext
        {
            Job = CronStoreTestContext.CreateJob("job-3161-wiring-ok"),
            RunId = RunId.From("run-wiring-ok"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Scheduled,
            Services = services
        };

        await new AgentPromptAction().ExecuteAsync(context);

        context.DeliveryError.ShouldBeNull();
    }

    // --- helpers ---

    /// <summary>
    /// Stands in for <c>CronTrigger</c> discovering that the job's pinned destination conversation
    /// no longer resolves and writing that back on the request.
    /// </summary>
    private sealed class DeliveryFailingTrigger(string? deliveryError) : IInternalTrigger
    {
        public TriggerType Type => TriggerType.Cron;
        public string DisplayName => "Test Cron Trigger";

        public Task<SessionId> CreateSessionAsync(
            AgentId agentId,
            string prompt,
            CancellationToken ct = default,
            InternalTriggerRequest? request = null)
        {
            if (request is not null)
                request.DeliveryError = deliveryError;

            return Task.FromResult(SessionId.From("cron:test:run"));
        }
    }

    private static CronJob AlertingJob(string id) => CronStoreTestContext.CreateJob(id, actionType: "boom") with
    {
        FailureAlertsEnabled = true,
        FailureAlertConversationId = ConversationId.From(AlertConversationId)
    };

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
    /// An action whose turn succeeds but whose PRIMARY delivery throws - the #3161 shape. It routes
    /// the delivery through the seam exactly as a real action does, so the test exercises the
    /// production containment rather than simulating its result.
    /// </summary>
    private sealed class FailingDeliveryAction(string actionType, string deliveryError) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => context.DeliverAsync(_ => throw new InvalidOperationException(deliveryError), cancellationToken);
    }

    private sealed class SucceedingDeliveryAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => context.DeliverAsync(_ => Task.CompletedTask, cancellationToken);
    }

    private sealed class SilentAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingAction(string actionType, string message) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
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

    private sealed class ThrowingAlertSink(string message) : ICronFailureAlertSink
    {
        public Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default)
            => throw new InvalidOperationException(message);
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
