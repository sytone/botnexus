using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #3209: an exception thrown by a cron action must not deposit its stack trace or its type
/// identity into durable run history, nor into the failure-alert body.
/// </summary>
/// <remarks>
/// <para>
/// The defect was a retention-policy mistake, not a crash: <c>ex.ToString()</c> was persisted to
/// <c>cron.sqlite</c> and re-exported to a conversation by the #2557/#3161 alert path. Every clause
/// below therefore asserts on the PERSISTED / DELIVERED text, because a test that only asserted the
/// run failed would have passed for the entire lifetime of the bug.
/// </para>
/// <para>
/// The negative assertions name the two artefacts that only <c>ToString()</c> produces: the
/// exception type's full name, and the canonical stack-frame prefix <c>"   at "</c>. Asserting the
/// message survives alongside them is what stops the fix from degenerating into "record nothing".
/// </para>
/// </remarks>
public sealed class CronErrorProjectionTests
{
    private const string AlertConversationId = "conv-3209-alerts";

    /// <summary>
    /// #3209 AC1 + AC5: the durable run-history detail (the job's <c>LastRunError</c>, which is the
    /// field <c>FinalizeRunAsync</c> writes and the one that carried <c>ex.ToString()</c>) contains
    /// neither the exception type's full name nor a stack frame - while still carrying the message.
    /// </summary>
    [Fact]
    public async Task ThrownAction_PersistsNeitherTheExceptionTypeNameNorAStackFrame()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-3209-a", actionType: "boom"));

        var scheduler = CreateScheduler(context.Store, [new ThrowingAction("boom", "kaboom went the job")]);

        var run = await scheduler.RunNowAsync(JobId.From("job-3209-a"));

        run.Status.ShouldBe(CronRunStatus.Error);

        var job = await context.Store.GetAsync(JobId.From("job-3209-a"));
        job.ShouldNotBeNull();
        var detail = job!.LastRunError;
        detail.ShouldNotBeNull();

        // AC2: the operator-facing diagnostic survives.
        detail!.ShouldContain("kaboom went the job");
        // AC1/AC5: the two things only ex.ToString() produces do not.
        detail.ShouldNotContain(typeof(InvalidOperationException).FullName!);
        detail.ShouldNotContain("   at ");
    }

    /// <summary>
    /// #3209 AC2: the run-history row's error summary is still <c>ex.Message</c> verbatim, so the
    /// projection did not cost operators the diagnostic they actually read.
    /// </summary>
    [Fact]
    public async Task ThrownAction_StillRecordsTheExceptionMessageAsTheRunErrorSummary()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-3209-b", actionType: "boom"));

        var scheduler = CreateScheduler(context.Store, [new ThrowingAction("boom", "kaboom went the job")]);

        await scheduler.RunNowAsync(JobId.From("job-3209-b"));

        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-3209-b"))).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Error);
        entry.Error.ShouldBe("kaboom went the job");
    }

    /// <summary>
    /// #3209 AC4: the failure alert delivered into a conversation carries the same projection. This
    /// is the clause that matters most - it is the only path by which the trace reached an agent's
    /// context window rather than merely a local sqlite file.
    /// </summary>
    [Fact]
    public async Task FailureAlertBody_CarriesTheProjectionRatherThanTheTrace()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-3209-c", actionType: "boom") with
        {
            FailureAlertsEnabled = true,
            FailureAlertConversationId = ConversationId.From(AlertConversationId)
        });

        var scheduler = CreateScheduler(context.Store, [new ThrowingAction("boom", "kaboom went the job")], sink);

        await scheduler.RunNowAsync(JobId.From("job-3209-c"));

        var alert = sink.Alerts.ShouldHaveSingleItem();
        alert.Error.ShouldNotBeNull();
        alert.Error!.ShouldContain("kaboom went the job");
        alert.Error.ShouldNotContain(typeof(InvalidOperationException).FullName!);
        alert.Error.ShouldNotContain("   at ");
    }

    /// <summary>
    /// #3209: an inner cause is where the actionable diagnostic usually lives, so the projection
    /// keeps the whole message chain - and still surrenders no type name or frame from either level.
    /// </summary>
    [Fact]
    public async Task WrappedException_KeepsTheInnerMessageWithoutTheInnerTypeName()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-3209-d", actionType: "boom"));

        var scheduler = CreateScheduler(
            context.Store,
            [new WrappingAction("boom", "outer failed", "the socket was closed")]);

        await scheduler.RunNowAsync(JobId.From("job-3209-d"));

        var job = await context.Store.GetAsync(JobId.From("job-3209-d"));
        var detail = job!.LastRunError;
        detail.ShouldNotBeNull();
        detail!.ShouldContain("outer failed");
        detail.ShouldContain("the socket was closed");
        detail.ShouldNotContain(typeof(InvalidOperationException).FullName!);
        detail.ShouldNotContain(typeof(TimeoutException).FullName!);
        detail.ShouldNotContain("   at ");
    }

    /// <summary>
    /// #3209: the guarantee is structural, not conventional. A wrapper that formats an inner
    /// <c>ToString()</c> INTO ITS OWN MESSAGE would smuggle the trace back through a projection
    /// that merely skipped <c>StackTrace</c>. The scrub is applied to the projected OUTPUT, so this
    /// hostile shape is still frame-free.
    /// </summary>
    [Fact]
    public void Project_ScrubsFramesEvenWhenTheMessageItselfEmbedsATrace()
    {
        Exception captured;
        try
        {
            throw new TimeoutException("inner boom");
        }
        catch (TimeoutException inner)
        {
            captured = inner;
        }

        // A message that literally contains a rendered exception, trace and all.
        var hostile = new InvalidOperationException($"wrapper saw: {captured}");

        var projected = CronErrorProjection.Project(hostile);

        projected.ShouldNotBeNull();
        projected!.ShouldContain("wrapper saw:");
        projected.ShouldNotContain("   at ");
    }

    /// <summary>#3209: a null exception projects to null rather than to an empty recorded error.</summary>
    [Fact]
    public void Project_ReturnsNull_ForNoException()
        => CronErrorProjection.Project(null).ShouldBeNull();

    /// <summary>
    /// #3209: a pathological message chain must not become an unbounded write into a durable store.
    /// </summary>
    [Fact]
    public void Project_CapsTheProjectedLength()
    {
        var projected = CronErrorProjection.Project(
            new InvalidOperationException(new string('x', CronErrorProjection.MaxProjectedLength * 3)));

        projected.ShouldNotBeNull();
        projected!.Length.ShouldBe(CronErrorProjection.MaxProjectedLength);
    }

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

    private sealed class ThrowingAction(string actionType, string message) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class WrappingAction(string actionType, string outer, string inner) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(outer, new TimeoutException(inner));
    }

    private sealed class RecordingAlertSink : ICronFailureAlertSink
    {
        private readonly List<CronFailureAlert> _alerts = [];

        public IReadOnlyList<CronFailureAlert> Alerts
        {
            get { lock (_alerts) { return _alerts.ToList(); } }
        }

        public Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default)
        {
            lock (_alerts) { _alerts.Add(alert); }
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
