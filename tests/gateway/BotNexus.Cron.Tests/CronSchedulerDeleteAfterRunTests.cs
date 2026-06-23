using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Behaviour for the opt-in <see cref="CronJob.DeleteAfterRun"/> ephemeral-run cleanup (#1561):
/// when a job opts in and the run produced a cron-scoped session, the scheduler deletes that
/// session (and its transcript) after the run completes, across every terminal path; otherwise
/// the session is left intact.
/// </summary>
public sealed class CronSchedulerDeleteAfterRunTests
{
    [Fact]
    public async Task DeleteAfterRun_DeletesCronSession_OnSuccess()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessionStore = new RecordingSessionStore();
        var action = new SessionRecordingAction("test-action", "cron:job-1:run-abc");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteAfterRun = true };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action], sessionStore);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("ok");
        sessionStore.Deleted.ShouldHaveSingleItem().Value.ShouldBe("cron:job-1:run-abc");
    }

    [Fact]
    public async Task DeleteAfterRun_DeletesCronSession_Once_OnError()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessionStore = new RecordingSessionStore();
        var action = new SessionThenThrowAction("test-action", "cron:job-1:run-err", "boom");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteAfterRun = true };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action], sessionStore);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        // The run is still recorded as a failure...
        run.Status.ShouldBe("error");
        run.Error.ShouldBe("boom");
        // ...and the ephemeral session is cleaned up exactly once even though the action threw.
        sessionStore.Deleted.Count.ShouldBe(1);
        sessionStore.Deleted.ShouldHaveSingleItem().Value.ShouldBe("cron:job-1:run-err");
    }

    [Fact]
    public async Task DeleteAfterRun_DeletesCronSession_OnTimeout()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessionStore = new RecordingSessionStore();
        // Records a session then blocks past the 1s timeout.
        var action = new SessionThenDelayAction("test-action", "cron:job-1:run-slow", TimeSpan.FromSeconds(10));
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteAfterRun = true };
        await context.Store.CreateAsync(job);
        var options = new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 1 };
        var scheduler = CreateScheduler(context.Store, [action], sessionStore, options);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("timed_out");
        sessionStore.Deleted.ShouldHaveSingleItem().Value.ShouldBe("cron:job-1:run-slow");
    }

    [Fact]
    public async Task DeleteAfterRun_NotSet_LeavesSessionIntact()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessionStore = new RecordingSessionStore();
        var action = new SessionRecordingAction("test-action", "cron:job-1:keepme");
        // DeleteAfterRun defaults to false.
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action], sessionStore);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("ok");
        sessionStore.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteAfterRun_NoSessionRecorded_DoesNotDelete()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessionStore = new RecordingSessionStore();
        // RecordingAction never records a session id.
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteAfterRun = true };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action], sessionStore);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("ok");
        sessionStore.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteAfterRun_NonCronSessionId_IsNotDeleted()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessionStore = new RecordingSessionStore();
        // A long-lived/per-agent session id that does NOT begin with "cron:" must never be deleted,
        // even if the flag is (mis)configured on a job whose action reuses such a session.
        var action = new SessionRecordingAction("test-action", "soul:agent-a:main");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteAfterRun = true };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action], sessionStore);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("ok");
        sessionStore.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteAfterRun_SessionStoreFailure_DoesNotMaskRunSuccess()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessionStore = new ThrowingSessionStore();
        var action = new SessionRecordingAction("test-action", "cron:job-1:run-x");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteAfterRun = true };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action], sessionStore);

        // Cleanup failure is best-effort: the run still succeeds and the delete attempt is swallowed.
        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("ok");
        sessionStore.DeleteAttempts.ShouldBe(1);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe("ok");
    }

    [Fact]
    public async Task DeleteAfterRun_RoundTripsThroughStore()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteAfterRun = true };
        await context.Store.CreateAsync(job);

        var fetched = await context.Store.GetAsync(JobId.From("job-1"));
        fetched.ShouldNotBeNull();
        fetched!.DeleteAfterRun.ShouldBeTrue();

        // Default remains false for jobs that don't opt in.
        var plain = CronStoreTestContext.CreateJob("job-2", actionType: "test-action");
        await context.Store.CreateAsync(plain);
        (await context.Store.GetAsync(JobId.From("job-2")))!.DeleteAfterRun.ShouldBeFalse();
    }

    [Fact]
    public async Task SyncConfiguredJobs_PersistsDeleteAfterRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var options = new CronOptions
        {
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["config-job"] = new()
                {
                    Name = "Ephemeral job",
                    Schedule = "*/5 * * * *",
                    ActionType = "agent-prompt",
                    AgentId = "agent-a",
                    Message = "hello",
                    DeleteAfterRun = true,
                    Enabled = true
                }
            }
        };
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("agent-prompt")], new RecordingSessionStore(), options);

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        var stored = await context.Store.GetAsync(JobId.From("config-job"));
        stored.ShouldNotBeNull();
        stored!.DeleteAfterRun.ShouldBeTrue();
    }

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        ISessionStore sessionStore,
        CronOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sessionStore);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            actions,
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(options ?? new CronOptions { Enabled = true, TickIntervalSeconds = 1 }),
            NullLogger<CronScheduler>.Instance);
    }

    private static async Task InvokeSyncConfiguredJobsAsync(CronScheduler scheduler, CronOptions options)
    {
        var method = typeof(CronScheduler).GetMethod("SyncConfiguredJobsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [options, CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    // ── Test actions ──────────────────────────────────────────────────────────

    private sealed class RecordingAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class SessionRecordingAction(string actionType, string sessionId) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            context.RecordSessionId(SessionId.From(sessionId));
            return Task.CompletedTask;
        }
    }

    private sealed class SessionThenThrowAction(string actionType, string sessionId, string message) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            context.RecordSessionId(SessionId.From(sessionId));
            throw new InvalidOperationException(message);
        }
    }

    private sealed class SessionThenDelayAction(string actionType, string sessionId, TimeSpan delay) : ICronAction
    {
        public string ActionType => actionType;
        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            context.RecordSessionId(SessionId.From(sessionId));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Test session stores ─────────────────────────────────────────────────────

    /// <summary>Records every <see cref="DeleteAsync"/> call; all other members no-op.</summary>
    private sealed class RecordingSessionStore : ISessionStore
    {
        public List<SessionId> Deleted { get; } = [];

        public Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        {
            Deleted.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<GatewaySession?>(null);
        public Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
        public Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(AgentId agentId, ChannelKey channelType, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
        public Task<IReadOnlyList<GatewaySession>> ListByConversationAsync(ConversationId conversationId, AgentId? agentId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
        public Task<IReadOnlyList<GatewaySession>> GetExistenceAsync(AgentId agentId, ExistenceQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
    }

    /// <summary>Throws on delete so the best-effort swallow path is exercised.</summary>
    private sealed class ThrowingSessionStore : ISessionStore
    {
        public int DeleteAttempts { get; private set; }

        public Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        {
            DeleteAttempts++;
            throw new InvalidOperationException("session store unavailable");
        }

        public Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<GatewaySession?>(null);
        public Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
        public Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(AgentId agentId, ChannelKey channelType, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
        public Task<IReadOnlyList<GatewaySession>> ListByConversationAsync(ConversationId conversationId, AgentId? agentId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
        public Task<IReadOnlyList<GatewaySession>> GetExistenceAsync(AgentId agentId, ExistenceQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
