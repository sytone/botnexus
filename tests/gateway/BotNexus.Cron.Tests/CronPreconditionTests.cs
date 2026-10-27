using System.Text.Json;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Cron.Actions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Shouldly;

namespace BotNexus.Cron.Tests;

public sealed class CronPreconditionTests
{
    [Theory]
    [InlineData("exit 10", "no_work", 0, 0)]
    [InlineData("exit 0", "ok", 1, 0)]
    [InlineData(null, "ok", 1, 0)]
    [InlineData("exit 2", "error", 0, 1)]
    [InlineData("throw 'probe failed'", "error", 0, 1)]
    [InlineData("Start-Sleep -Seconds 30", "error", 0, 1)]
    public async Task Probe_DecidesBeforeProviderAndPersistsOutcome(string? command, string status, int requests, int alerts)
    {
        await using var fixture = new Fixture();
        var job = Job(command) with
        {
            FailureAlertsEnabled = true,
            FailureAlertConversationId = ConversationId.From("alerts"),
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = command == "Start-Sleep -Seconds 30" ? 1 : 15 }
        };
        await fixture.Store.CreateAsync(job);
        var run = await fixture.Scheduler.RunNowAsync(job.Id);
        run.Status.ShouldBe(status, run.Error);
        fixture.Provider.Requests.ShouldBe(requests);
        fixture.Alerts.Count.ShouldBe(alerts);
        var history = await fixture.Store.GetRunHistoryAsync(job.Id);
        history.ShouldHaveSingleItem().Status.ShouldBe(status);
        (await fixture.Store.GetAsync(job.Id)).ShouldNotBeNull().LastRunStatus.ShouldBe(status);
        if (status == "no_work")
        {
            run.Error.ShouldBeNull();
            run.SessionId.ShouldBeNull();
            fixture.Alerts.Count.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Denial_PreventsProcessAndProvider_UsesFailureAlert()
    {
        await using var fixture = new Fixture(allowed: false);
        var marker = Path.Combine(fixture.Directory, "must-not-exist").Replace("'", "''");
        var job = Job($"Set-Content -LiteralPath '{marker}' -Value started; exit 0") with
        {
            FailureAlertsEnabled = true, FailureAlertConversationId = ConversationId.From("alerts")
        };
        await fixture.Store.CreateAsync(job);
        var run = await fixture.Scheduler.RunNowAsync(job.Id);
        run.Status.ShouldBe("error");
        fixture.Authorization.Firings.ShouldBe(1);
        File.Exists(marker).ShouldBeFalse();
        fixture.Provider.Requests.ShouldBe(0);
        fixture.Alerts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task FourOutcomes_AreDistinct_AndExpiryHasNoHistory()
    {
        await using var fixture = new Fixture();
        var statuses = new List<string>();
        foreach (var command in new[] { "exit 10", "exit 0", "exit 2" })
        {
            var job = Job(command) with { Id = JobId.From(Guid.NewGuid().ToString("N")) };
            await fixture.Store.CreateAsync(job);
            statuses.Add((await fixture.Scheduler.RunNowAsync(job.Id)).Status);
        }
        var expired = Job("exit 0") with { Id = JobId.From("expired"), ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) };
        await fixture.Store.CreateAsync(expired);
        statuses.Add((await fixture.Scheduler.RunNowAsync(expired.Id)).Status);
        statuses.ShouldBe(new[] { "no_work", "ok", "error", "skipped" });
        statuses.Distinct().Count().ShouldBe(4);
        (await fixture.Store.GetRunHistoryAsync(expired.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task OrdinaryCommand_Exit10IsStillFailure()
    {
        await using var fixture = new Fixture();
        var job = Job(null) with { ActionType = "command", ShellCommand = "exit 10" };
        await fixture.Store.CreateAsync(job);
        (await fixture.Scheduler.RunNowAsync(job.Id)).Status.ShouldBe("error");
        fixture.Provider.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task HostCancellationDuringProbe_RecordsTerminalFailureWithoutProvider()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Authorization.OnFiring = cancellation.Cancel;
        var job = Job("exit 0");
        await fixture.Store.CreateAsync(job);
        await Should.ThrowAsync<OperationCanceledException>(async () => await fixture.Scheduler.RunNowAsync(job.Id, cancellation.Token));
        (await fixture.Store.GetRunHistoryAsync(job.Id)).ShouldHaveSingleItem().Status.ShouldBe("error");
        fixture.Provider.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task ExecutionClass_NegativeProbeIsNotZeroToolFailure()
    {
        await using var fixture = new Fixture();
        var job = Job("exit 10") with { ExecutionClass = true };
        await fixture.Store.CreateAsync(job);
        (await fixture.Scheduler.RunNowAsync(job.Id)).Status.ShouldBe("no_work");
        fixture.Provider.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task Store_RoundTripsUpdatesClearsAndMigratesPrecondition()
    {
        await using var fixture = new Fixture();
        var job = await fixture.Store.CreateAsync(Job("exit 10"));
        ReadProbe((await fixture.Store.GetAsync(job.Id)).ShouldNotBeNull()).ShouldBe("exit 10");
        ReadProbe((await fixture.Store.ListAsync()).ShouldHaveSingleItem()).ShouldBe("exit 10");
        var changed = WithProbe(job, "exit 0");
        await fixture.Store.UpdateDefinitionAsync(changed);
        ReadProbe((await fixture.Store.GetAsync(job.Id)).ShouldNotBeNull()).ShouldBe("exit 0");
        await fixture.Store.UpdateDefinitionAsync(WithProbe(changed, null));
        ReadProbe((await fixture.Store.GetAsync(job.Id)).ShouldNotBeNull()).ShouldBeNull();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.Db};Mode=ReadWriteCreate"))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.Db};Pooling=False"))
        {
            await connection.OpenAsync();
            using var sql = connection.CreateCommand();
            sql.CommandText = "ALTER TABLE cron_jobs DROP COLUMN precondition_command";
            await sql.ExecuteNonQueryAsync();
        }
        var migrated = new SqliteCronStore(fixture.Db);
        await migrated.InitializeAsync();
        ReadProbe((await migrated.GetAsync(job.Id)).ShouldNotBeNull()).ShouldBeNull();
    }

    [Fact]
    public async Task PurgeRunsOlderThanAsync_AgedNoWorkIsPurgedAndRunningIsRetained()
    {
        await using var fixture = new Fixture();
        var job = await fixture.Store.CreateAsync(Job("exit 10"));
        var old = DateTimeOffset.UtcNow.AddDays(-60);
        var cutoff = old.AddDays(30);
        var completed = await fixture.Store.RecordRunStartAsync(job.Id);
        await fixture.Store.RecordRunCompleteAsync(completed.Id, CronRunStatus.NoWork);
        var running = await fixture.Store.RecordRunStartAsync(job.Id);
        var recent = await fixture.Store.RecordRunStartAsync(job.Id);
        await fixture.Store.RecordRunCompleteAsync(recent.Id, CronRunStatus.NoWork);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.Db};Pooling=False"))
        {
            await connection.OpenAsync();
            using var sql = connection.CreateCommand();
            sql.CommandText = "UPDATE cron_runs SET started_at = $old, completed_at = CASE WHEN status = $running THEN NULL ELSE $old END WHERE id IN ($completed, $inflight)";
            sql.Parameters.AddWithValue("$old", old.ToString("O"));
            sql.Parameters.AddWithValue("$running", CronRunStatus.Running);
            sql.Parameters.AddWithValue("$completed", completed.Id.Value);
            sql.Parameters.AddWithValue("$inflight", running.Id.Value);
            (await sql.ExecuteNonQueryAsync()).ShouldBe(2);
        }
        (await fixture.Store.PurgeRunsOlderThanAsync(cutoff)).ShouldBe(1);
        (await fixture.Store.GetRunAsync(completed.Id)).ShouldBeNull();
        var retained = (await fixture.Store.GetRunAsync(running.Id)).ShouldNotBeNull();
        retained.Status.ShouldBe(CronRunStatus.Running);
        retained.CompletedAt.ShouldBeNull();
        retained.StartedAt.ShouldBe(old);
        (await fixture.Store.GetRunAsync(recent.Id)).ShouldNotBeNull().Status.ShouldBe(CronRunStatus.NoWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostCancellationDuringNegativePersistence_RecordsTerminalFailureWithoutRetryOrAlert(bool duringFinalization)
    {
        using var cancellation = new CancellationTokenSource();
        var completedWrites = new List<(string Status, CancellationToken Token)>();
        var finalizedWrites = new List<(string Status, CancellationToken Token)>();
        await using var fixture = new Fixture(decorateStore: store =>
        {
            var decorated = new Mock<ICronStore>(MockBehavior.Strict);
            decorated.Setup(x => x.InitializeAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken ct) => store.InitializeAsync(ct));
            decorated.Setup(x => x.GetAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
                .Returns((JobId id, CancellationToken ct) => store.GetAsync(id, ct));
            decorated.Setup(x => x.RecordRunStartAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
                .Returns((JobId id, CancellationToken ct) => store.RecordRunStartAsync(id, ct));
            decorated.Setup(x => x.RecordRunCompleteAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<SessionId?>(), It.IsAny<CronRunCost?>(), It.IsAny<CancellationToken>()))
                .Returns(async (RunId id, string status, string? error, SessionId? session, CronRunCost? cost, CancellationToken ct) =>
                {
                    completedWrites.Add((status, ct));
                    if (status == CronRunStatus.NoWork && !duringFinalization) cancellation.Cancel();
                    await store.RecordRunCompleteAsync(id, status, error, session, cost, ct);
                });
            decorated.Setup(x => x.RecordRunFinalizationAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns(async (JobId id, DateTimeOffset at, string status, string? error, CancellationToken ct) =>
                {
                    finalizedWrites.Add((status, ct));
                    if (status == CronRunStatus.NoWork && duringFinalization) cancellation.Cancel();
                    await store.RecordRunFinalizationAsync(id, at, status, error, ct);
                });
            return decorated.Object;
        });
        var marker = Path.Combine(fixture.Directory, "probe-invocations").Replace("'", "''");
        var job = Job($"Add-Content -LiteralPath '{marker}' -Value invoked; exit 10") with
        {
            FailureAlertsEnabled = true, FailureAlertConversationId = ConversationId.From("alerts")
        };
        await fixture.Store.CreateAsync(job);
        await Should.ThrowAsync<OperationCanceledException>(async () => await fixture.Scheduler.RunNowAsync(job.Id, cancellation.Token));
        cancellation.IsCancellationRequested.ShouldBeTrue();
        var history = (await fixture.Store.GetRunHistoryAsync(job.Id)).ShouldHaveSingleItem();
        history.Status.ShouldBe(CronRunStatus.Error);
        history.CompletedAt.ShouldNotBeNull();
        history.Error.ShouldNotBeNullOrWhiteSpace();
        history.SessionId.ShouldBeNull();
        var persistedJob = (await fixture.Store.GetAsync(job.Id)).ShouldNotBeNull();
        persistedJob.LastRunStatus.ShouldBe(CronRunStatus.Error);
        persistedJob.LastRunError.ShouldBe(history.Error);
        completedWrites.Select(x => x.Status).ShouldBe(new[] { CronRunStatus.NoWork, CronRunStatus.Error });
        finalizedWrites.Select(x => x.Status).ShouldBe(duringFinalization
            ? new[] { CronRunStatus.NoWork, CronRunStatus.Error } : new[] { CronRunStatus.Error });
        completedWrites[0].Token.ShouldBe(cancellation.Token);
        completedWrites[^1].Token.CanBeCanceled.ShouldBeFalse();
        finalizedWrites[^1].Token.CanBeCanceled.ShouldBeFalse();
        fixture.Authorization.Firings.ShouldBe(1);
        File.ReadAllLines(marker).ShouldBe(new[] { "invoked" });
        fixture.Provider.Requests.ShouldBe(0);
        fixture.Alerts.Count.ShouldBe(0);
    }

    private static string? ReadProbe(CronJob job)
    {
        var property = typeof(CronJob).GetProperty("PreconditionCommand").ShouldNotBeNull();
        return property.GetValue(job) as string;
    }

    private static CronJob WithProbe(CronJob job, string? command)
    {
        var json = JsonSerializer.SerializeToNode(job).ShouldNotBeNull();
        json["PreconditionCommand"] = command;
        return json.Deserialize<CronJob>().ShouldNotBeNull();
    }

    private static CronJob Job(string? command) => WithProbe(new CronJob
    {
        Id = JobId.From("probe"), Name = "Probe", Schedule = "* * * * *", ActionType = "agent-prompt",
        AgentId = AgentId.From("owner"), Message = "Do work", CreatedBy = "owner"
    }, command);

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Environment.GetEnvironmentVariable("BOTNEXUS_3351_TEST_ROOT") ?? Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string Db => Path.Combine(Directory, "cron.db");
        public SqliteCronStore Store { get; }
        public CountingProvider Provider { get; } = new();
        public AlertSink Alerts { get; } = new();
        public Authorizer Authorization { get; }
        public CronScheduler Scheduler { get; }
        private readonly ServiceProvider _services;
        public Fixture(bool allowed = true, Func<SqliteCronStore, ICronStore>? decorateStore = null)
        {
            Authorization = new Authorizer(allowed);
            Store = new SqliteCronStore(Db);
            _services = new ServiceCollection().AddSingleton<IInternalTrigger>(new ProviderTrigger(Provider))
                .AddSingleton<ICommandCronAuthorizer>(Authorization).AddSingleton<ICronFailureAlertSink>(Alerts)
                .AddSingleton<ISecretRedactor>(new Redactor()).BuildServiceProvider();
            Scheduler = new CronScheduler(decorateStore?.Invoke(Store) ?? Store, [new AgentPromptAction(), new CommandCronAction()],
                _services.GetRequiredService<IServiceScopeFactory>(), new Monitor(), NullLogger<CronScheduler>.Instance);
        }
        public ValueTask DisposeAsync()
        {
            Scheduler.Dispose();
            _services.Dispose();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Db};Mode=ReadWriteCreate");
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Authorizer(bool allowed) : ICommandCronAuthorizer
    {
        public int Firings { get; private set; }
        public Action? OnFiring { get; set; }
        public CommandAuthorizationDecision AuthorizeFiring(CronJob job, string command)
        {
            Firings++;
            OnFiring?.Invoke();
            return allowed ? CommandAuthorizationDecision.Allow("test") : CommandAuthorizationDecision.Deny("test denial");
        }
        public CommandAuthorizationDecision AuthorizeAuthoring(CronJob job, string command) => allowed ? CommandAuthorizationDecision.Allow("test") : CommandAuthorizationDecision.Deny("test denial");
    }
    private sealed class CountingProvider : IApiProvider
    {
        public string Api => "test";
        public int Requests { get; private set; }
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null) { Requests++; return new LlmStream(); }
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null) { Requests++; return new LlmStream(); }
    }
    private sealed class ProviderTrigger(IApiProvider provider) : IInternalTrigger
    {
        public TriggerType Type => TriggerType.Cron;
        public string DisplayName => "Isolated provider trigger";
        public Task<SessionId> CreateSessionAsync(AgentId agentId, string message, CancellationToken cancellationToken = default, InternalTriggerRequest? request = null)
        {
            _ = provider.StreamSimple(new LlmModel("test", "Test", "test", "test", "https://example.invalid", false, ["text"], new ModelCost(0,0,0,0), 1000, 100), new Context(null, []));
            return Task.FromResult(SessionId.From("cron:probe:run"));
        }
    }
    private sealed class AlertSink : ICronFailureAlertSink
    {
        public int Count { get; private set; }
        public Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default) { Count++; return Task.CompletedTask; }
    }
    private sealed class Redactor : ISecretRedactor
    {
        public string Redact(string input) => input;
        public string RedactForExternalDelivery(string input) => input;
    }
    private sealed class Monitor : IOptionsMonitor<CronOptions>
    {
        public CronOptions CurrentValue { get; } = new();
        public CronOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CronOptions, string?> listener) => null;
    }
}
