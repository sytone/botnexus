using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2557: opt-in per-job cron failure alerts carrying the scheduled run time.
/// </summary>
public sealed class CronFailureAlertTests
{
    private const string AlertConversationId = "conv-alerts";

    [Fact]
    public async Task FailingRun_WithAlertsDisabled_ProducesNoDelivery()
    {
        // AC1 / AC6: default is off; behaviour parity with today.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        var job = CronStoreTestContext.CreateJob("job-off", actionType: "boom");
        job.FailureAlertsEnabled.ShouldBeFalse("failure alerts must be OFF by default");
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [new ThrowingAction("boom", "kaboom")], sink);
        await scheduler.RunNowAsync(JobId.From("job-off"));

        sink.Alerts.ShouldBeEmpty();
    }

    [Fact]
    public async Task FailingRun_WithAlertsEnabled_DeliversExactlyOneMessage()
    {
        // AC2
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        await context.Store.CreateAsync(AlertingJob("job-on"));

        var scheduler = CreateScheduler(context.Store, [new ThrowingAction("boom", "kaboom")], sink);
        await scheduler.RunNowAsync(JobId.From("job-on"));

        sink.Alerts.Count.ShouldBe(1);
        sink.Alerts[0].ConversationId.Value.ShouldBe(AlertConversationId);
    }

    [Fact]
    public async Task Alert_CarriesJobNameScheduledRunTimeAndConsecutiveCount()
    {
        // AC3: the scheduled run time is the whole point -- without it the recipient
        // cannot tell which occurrence broke.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        await context.Store.CreateAsync(AlertingJob("job-payload"));

        var scheduled = DateTimeOffset.UtcNow.AddMinutes(-7);
        var scheduler = CreateScheduler(context.Store, [new ThrowingAction("boom", "kaboom")], sink);
        await InvokeRunActionAsync(scheduler, await context.Store.GetAsync(JobId.From("job-payload")) ?? throw new InvalidOperationException(), scheduled);

        var alert = sink.Alerts.ShouldHaveSingleItem().Alert;
        alert.JobId.Value.ShouldBe("job-payload");
        alert.JobName.ShouldBe("Job job-payload");
        alert.ScheduledRunTime.ShouldBe(scheduled);
        alert.AttemptedAt.ShouldBeGreaterThanOrEqualTo(scheduled);
        alert.ConsecutiveErrorCount.ShouldBe(1);

        var rendered = alert.FormatMessage();
        rendered.ShouldContain("Job job-payload");
        rendered.ShouldContain(scheduled.ToString("O"));
        rendered.ShouldContain("Consecutive errors: 1");
    }

    [Fact]
    public async Task Alert_ErrorText_IsPassedThroughExternalDeliveryRedactor()
    {
        // AC4
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        await context.Store.CreateAsync(AlertingJob("job-redact"));

        var scheduler = CreateScheduler(
            context.Store,
            [new ThrowingAction("boom", "failed with token=hunter2")],
            sink,
            redactor: new StubRedactor());

        await scheduler.RunNowAsync(JobId.From("job-redact"));

        var alert = sink.Alerts.ShouldHaveSingleItem().Alert;
        alert.Error.ShouldNotBeNull();
        alert.Error!.ShouldNotContain("hunter2");
        alert.Error.ShouldContain("[EXT-REDACTED]");
    }

    [Fact]
    public async Task FiveConsecutiveFailures_ProduceThreeAlerts_BecauseOfBackoff()
    {
        // AC5 / AC8: the backoff clause is what keeps this below five. Disabling it
        // (see the non-vacuity mutation) makes this test fail.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        await context.Store.CreateAsync(AlertingJob("job-backoff"));

        var scheduler = CreateScheduler(context.Store, [new ThrowingAction("boom", "kaboom")], sink);

        for (var i = 0; i < 5; i++)
            await scheduler.RunNowAsync(JobId.From("job-backoff"));

        // Streak positions 1, 2 and 4 alert; 3 and 5 are suppressed by backoff.
        sink.Alerts.Count.ShouldBe(3);
        sink.Alerts.Select(a => a.Alert.ConsecutiveErrorCount).ShouldBe(new[] { 1, 2, 4 });
    }

    [Fact]
    public async Task AlertDeliveryFailure_DoesNotFailTheCronRun_AndIsLogged()
    {
        // AC7
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new ThrowingAlertSink();
        var logger = new CapturingLogger();
        await context.Store.CreateAsync(AlertingJob("job-sinkfail"));

        var scheduler = CreateScheduler(
            context.Store,
            [new ThrowingAction("boom", "kaboom")],
            sink,
            logger: logger);

        var run = await scheduler.RunNowAsync(JobId.From("job-sinkfail"));

        // The run's own terminal state is untouched by the delivery failure.
        run.Status.ShouldBe(CronRunStatus.Error);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-sinkfail"));
        history.ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.Error);
        logger.Messages.ShouldContain(m => m.Contains("failure alert", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LegacyJobRowPredatingAlertColumns_DoesNotAlertAndDoesNotCrash()
    {
        // The read-path-trusting-unwritten-state trap (#2488/#2324/#2340/#2548/#2556):
        // a row written before the alert columns existed must read as alerts-off.
        var tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-cron-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var dbPath = Path.Combine(tempDirectory, "cron.db");
        try
        {
            await WriteLegacySchemaRowAsync(dbPath, "legacy-job");

            var store = new SqliteCronStore(dbPath, new FileSystem());
            await store.InitializeAsync();

            var job = await store.GetAsync(JobId.From("legacy-job"));
            job.ShouldNotBeNull();
            job!.FailureAlertsEnabled.ShouldBeFalse();
            job.FailureAlertConversationId.ShouldBeNull();

            var sink = new RecordingAlertSink();
            var scheduler = CreateScheduler(store, [new ThrowingAction("boom", "kaboom")], sink);
            var run = await scheduler.RunNowAsync(JobId.From("legacy-job"));

            run.Status.ShouldBe(CronRunStatus.Error);
            sink.Alerts.ShouldBeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDirectory, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task WriteLegacySchemaRowAsync(string dbPath, string jobId)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            // Pre-#2557 schema: no failure_alerts_enabled / failure_alert_conversation_id columns.
            create.CommandText = """
                CREATE TABLE cron_jobs (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    schedule TEXT NOT NULL,
                    action_type TEXT NOT NULL,
                    agent_id TEXT NULL,
                    message TEXT NULL,
                    template_name TEXT NULL,
                    template_parameters_json TEXT NULL,
                    model TEXT NULL,
                    webhook_url TEXT NULL,
                    shell_command TEXT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    system INTEGER NOT NULL DEFAULT 0,
                    time_zone TEXT NULL,
                    created_by TEXT NULL,
                    created_at TEXT NOT NULL,
                    last_run_at TEXT NULL,
                    next_run_at TEXT NULL,
                    last_run_status TEXT NULL,
                    last_run_error TEXT NULL,
                    metadata_json TEXT NULL,
                    conversation_id TEXT NULL,
                    delete_after_run INTEGER NOT NULL DEFAULT 0,
                    schedule_activated_at TEXT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO cron_jobs (id, name, schedule, action_type, agent_id, enabled, system, created_at)
            VALUES ($id, $name, '*/1 * * * *', 'boom', 'agent-a', 1, 0, $createdAt)
            """;
        insert.Parameters.AddWithValue("$id", jobId);
        insert.Parameters.AddWithValue("$name", "Legacy Job");
        insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync();
    }

    private static CronJob AlertingJob(string id) => CronStoreTestContext.CreateJob(id, actionType: "boom") with
    {
        FailureAlertsEnabled = true,
        FailureAlertConversationId = ConversationId.From(AlertConversationId)
    };

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        ICronFailureAlertSink? sink = null,
        ISecretRedactor? redactor = null,
        ILogger<CronScheduler>? logger = null)
    {
        var services = new ServiceCollection();
        if (sink is not null)
            services.AddSingleton(sink);
        services.AddSingleton<ISecretRedactor>(redactor ?? new StubRedactor());
        var provider = services.BuildServiceProvider();
        return new CronScheduler(
            store,
            actions,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions { Enabled = true, TickIntervalSeconds = 1 }),
            logger ?? NullLogger<CronScheduler>.Instance);
    }

    private static async Task InvokeRunActionAsync(CronScheduler scheduler, CronJob job, DateTimeOffset triggeredAt)
    {
        var method = typeof(CronScheduler).GetMethod("RunActionAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [job, CronTriggerType.Scheduled, triggeredAt, CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
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

    private sealed class ThrowingAlertSink : ICronFailureAlertSink
    {
        public Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default)
            => throw new InvalidOperationException("sink is down");
    }

    private sealed class StubRedactor : ISecretRedactor
    {
        public string Redact(string input) => input;

        public string RedactForExternalDelivery(string input)
            => input.Replace("hunter2", "[EXT-REDACTED]", StringComparison.Ordinal);
    }

    private sealed class ThrowingAction(string actionType, string message) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class CapturingLogger : ILogger<CronScheduler>
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
