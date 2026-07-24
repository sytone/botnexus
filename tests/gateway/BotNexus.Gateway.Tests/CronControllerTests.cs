using System.IO.Abstractions;
using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests;

public sealed class CronControllerTests
{
    [Fact]
    public async Task List_ReturnsAllJobs()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1"));
        await store.CreateAsync(CreateJob("job-2"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions());

        var result = await controller.List(CancellationToken.None);

        var jobs = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJob>;
        jobs.ShouldNotBeNull();
        jobs!.Count().ShouldBe(2);
    }

    [Fact]
    public async Task Get_ReturnsSpecificJob()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions());

        var result = await controller.Get("job-1", CancellationToken.None);

        var job = (result.Result as OkObjectResult)?.Value as CronJob;
        job.ShouldNotBeNull();
        job!.Id.Value.ShouldBe("job-1");
    }

    [Fact]
    public async Task Create_ReturnsCreated()
    {
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        var request = CreateJob("job-create");

        var result = await controller.Create(request, CancellationToken.None);

        var created = (result.Result as CreatedAtActionResult)?.Value as CronJob;
        created.ShouldNotBeNull();
        created!.Id.Value.ShouldBe("job-create");
        (await store.GetAsync(created.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Delete_ReturnsNoContent()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions());

        var result = await controller.Delete("job-1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        (await store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    [Fact]
    public async Task RunNow_TriggersExecution()
    {
        var action = new RecordingAction();
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1", actionType: action.ActionType));
        var controller = CreateController(store, action, new CronOptions());

        var result = await controller.Run("job-1", CancellationToken.None);

        var run = (result.Result as AcceptedResult)?.Value as CronRun;
        run.ShouldNotBeNull();
        run!.Status.ShouldBe("ok");
        action.ExecutionCount.ShouldBe(1);
    }

    [Fact]
    public async Task List_IncludesConfiguredJobs_AndNormalizesAgentChat()
    {
        var store = new FakeCronStore();
        var options = new CronOptions
        {
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["config-job"] = new()
                {
                    Name = "Configured Job",
                    Schedule = "*/5 * * * *",
                    ActionType = "agent-chat",
                    AgentId = "agent-a",
                    Message = "hello",
                    Model = "openai/gpt-4.1",
                    Enabled = true
                }
            }
        };
        var controller = CreateController(store, new RecordingAction(), options);

        var result = await controller.List(CancellationToken.None);

        var jobs = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJob>;
        jobs.ShouldNotBeNull();
        var configured = jobs!.Single(job => job.Id.Value == "config-job");
        configured.ActionType.ShouldBe("agent-prompt");
        configured.Model.ShouldBe("openai/gpt-4.1");
    }

    [Fact]
    public async Task Create_NormalizesAgentChat_AndPersistsModel()
    {
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        var request = CreateJob("job-norm") with
        {
            ActionType = "agent-chat",
            Model = "openai/gpt-4.1"
        };

        var result = await controller.Create(request, CancellationToken.None);

        var created = (result.Result as CreatedAtActionResult)?.Value as CronJob;
        created.ShouldNotBeNull();
        created!.ActionType.ShouldBe("agent-prompt");
        created.Model.ShouldBe("openai/gpt-4.1");
    }

    [Fact]
    public async Task Create_WithFarFutureNextRunAt_ReturnsBadRequest()
    {
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        // Year 10000 is beyond the MaxAllowedTimestamp ceiling of 9000-01-01
        var request = CreateJob("job-future") with
        {
            NextRunAt = new DateTimeOffset(9001, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var result = await controller.Create(request, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_WithPreEpochNextRunAt_ReturnsBadRequest()
    {
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        // Before 1970-01-01 (the MinAllowedTimestamp floor)
        var request = CreateJob("job-preepoch") with
        {
            NextRunAt = new DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.Zero)
        };

        var result = await controller.Create(request, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_WithFarFutureNextRunAt_ReturnsBadRequest()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-upd"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        var request = CreateJob("job-upd") with
        {
            NextRunAt = new DateTimeOffset(9001, 6, 15, 0, 0, 0, TimeSpan.Zero)
        };

        var result = await controller.Update("job-upd", request, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_WithValidNearFutureNextRunAt_ReturnsCreated()
    {
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        var futureTime = DateTimeOffset.UtcNow.AddHours(1);
        var request = CreateJob("job-validnext") with { NextRunAt = futureTime };

        var result = await controller.Create(request, CancellationToken.None);

        var created = (result.Result as CreatedAtActionResult)?.Value as CronJob;
        created.ShouldNotBeNull();
        created!.NextRunAt.ShouldBe(futureTime);
    }

    [Fact]
    public async Task Create_WithOutOfRangeCreatedAt_ReturnsBadRequest()
    {
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        var request = CreateJob("job-badcreated") with
        {
            CreatedAt = new DateTimeOffset(9001, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var result = await controller.Create(request, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    // #2133 controller-path same-job concurrency seam: a paused controller definition update
    // (routed through the narrow UpdateDefinitionAsync + SetNextRunAtAsync writes) racing the
    // scheduler's narrow runtime/conversation writes on the SAME job must not regress run
    // status, timestamps, next run, or the conversation pin. Uses a real SQLite store.
    [Fact]
    public async Task Update_RacingSchedulerRuntimeWrites_PreservesRuntimeAndConversation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-cron-ctrl-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(tempDir, "cron.db");
        var store = new SqliteCronStore(dbPath, new FileSystem());
        try
        {
            await store.InitializeAsync();
            await store.CreateAsync(CreateJob("job-1") with { Schedule = "*/5 * * * *" });
            var jobId = JobId.From("job-1");

            var scheduler = new CronScheduler(
                store,
                [new RecordingAction()],
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new StaticOptionsMonitor<CronOptions>(new CronOptions()),
                NullLogger<CronScheduler>.Instance);
            var controller = new CronController(
                store, scheduler, new StaticOptionsMonitor<CronOptions>(new CronOptions()), NullLogger<CronController>.Instance);

            var runAt = DateTimeOffset.UtcNow;
            var nextRun = runAt.AddMinutes(5);

            var controllerUpdate = Task.Run(async () =>
                await controller.Update("job-1", CreateJob("job-1") with { Schedule = "*/5 * * * *", Name = "Edited", Enabled = false }, CancellationToken.None));

            var runtimeWrites = Task.Run(async () =>
            {
                await store.RecordRunFinalizationAsync(jobId, runAt, "ok", null);
                await store.SetNextRunAtAsync(jobId, nextRun);
                await store.TrySetConversationIdAsync(jobId, ConversationId.From("conv-1"));
            });

            await Task.WhenAll(controllerUpdate, runtimeWrites);

            var loaded = await store.GetAsync(jobId);
            loaded.ShouldNotBeNull();
            loaded!.Name.ShouldBe("Edited");
            loaded.Enabled.ShouldBeFalse();
            loaded.LastRunStatus.ShouldBe("ok");
            loaded.NextRunAt.ShouldNotBeNull();
            loaded.NextRunAt!.Value.ShouldBe(nextRun, TimeSpan.FromSeconds(1));
            loaded.ConversationId!.Value.Value.ShouldBe("conv-1");
        }
        finally
        {
            SqliteConnectionClearHelper();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
            }
        }
    }

    private static void SqliteConnectionClearHelper()
        => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private static CronController CreateController(FakeCronStore store, ICronAction action, CronOptions options)
    {
        var scheduler = new CronScheduler(
            store,
            [action],
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions()),
            NullLogger<CronScheduler>.Instance);
        return new CronController(store, scheduler, new StaticOptionsMonitor<CronOptions>(options), NullLogger<CronController>.Instance);
    }

    private static CronJob CreateJob(string id, string actionType = "agent-prompt")
        => new()
        {
            Id = JobId.From(id),
            Name = "Test Job",
            Schedule = "*/1 * * * *",
            ActionType = actionType,
            AgentId = AgentId.From("agent-a"),
            Message = "run",
            Enabled = true,
            CreatedBy = "tester",
            CreatedAt = DateTimeOffset.UtcNow
        };

    private sealed class RecordingAction : ICronAction
    {
        public int ExecutionCount { get; private set; }
        public string ActionType => "agent-prompt";

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCronStore : ICronStore
    {
        private readonly Dictionary<string, CronJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CronRun> _runs = new(StringComparer.OrdinalIgnoreCase);

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<CronJob> CreateAsync(CronJob job, CancellationToken ct = default)
        {
            var created = job with
            {
                CreatedAt = job.CreatedAt == default ? DateTimeOffset.UtcNow : job.CreatedAt
            };
            _jobs[created.Id.Value] = created;
            return Task.FromResult(created);
        }

        public Task<CronJob?> GetAsync(JobId jobId, CancellationToken ct = default)
            => Task.FromResult(_jobs.GetValueOrDefault(jobId.Value));

        public Task<IReadOnlyList<CronJob>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
        {
            IReadOnlyList<CronJob> jobs = !agentId.HasValue
                ? [.. _jobs.Values]
                : _jobs.Values.Where(job => job.AgentId.HasValue && job.AgentId.Value == agentId.Value).ToList();
            return Task.FromResult(jobs);
        }

        public Task<CronJob?> UpdateDefinitionAsync(CronJob job, CancellationToken ct = default)
        {
            // Mirror the SQLite narrow definition write: preserve scheduler-owned runtime
            // bookkeeping (LastRun*/NextRunAt) and the CAS-pinned conversation from the stored
            // row; only the caller-authored definition columns are overwritten (#2133).
            if (!_jobs.TryGetValue(job.Id.Value, out var existing))
                return Task.FromResult<CronJob?>(null);

            var merged = job with
            {
                CreatedAt = existing.CreatedAt,
                LastRunAt = existing.LastRunAt,
                NextRunAt = existing.NextRunAt,
                LastRunStatus = existing.LastRunStatus,
                LastRunError = existing.LastRunError,
                ConversationId = existing.ConversationId
            };
            _jobs[job.Id.Value] = merged;
            return Task.FromResult<CronJob?>(merged);
        }

        public Task SetNextRunAtAsync(JobId jobId, DateTimeOffset? nextRunAt, CancellationToken ct = default)
        {
            if (_jobs.TryGetValue(jobId.Value, out var existing))
                _jobs[jobId.Value] = existing with { NextRunAt = nextRunAt };
            return Task.CompletedTask;
        }

        public Task RecordRunFinalizationAsync(JobId jobId, DateTimeOffset lastRunAt, string lastRunStatus, string? lastRunError, CancellationToken ct = default)
        {
            if (_jobs.TryGetValue(jobId.Value, out var existing))
                _jobs[jobId.Value] = existing with
                {
                    LastRunAt = lastRunAt,
                    LastRunStatus = lastRunStatus,
                    LastRunError = lastRunError
                };
            return Task.CompletedTask;
        }

        public Task DeleteAsync(JobId jobId, CancellationToken ct = default)
        {
            _jobs.Remove(jobId.Value);
            foreach (var runId in _runs.Values.Where(run => run.JobId == jobId).Select(run => run.Id.Value).ToList())
                _runs.Remove(runId);
            return Task.CompletedTask;
        }

        public Task<ConversationId?> TrySetConversationIdAsync(JobId jobId, ConversationId conversationId, CancellationToken ct = default)
        {
            if (!_jobs.TryGetValue(jobId.Value, out var job))
                return Task.FromResult<ConversationId?>(null);

            if (!job.ConversationId.HasValue)
            {
                _jobs[jobId.Value] = job with { ConversationId = conversationId };
                return Task.FromResult<ConversationId?>(conversationId);
            }

            return Task.FromResult<ConversationId?>(job.ConversationId);
        }

        public Task<CronRun> RecordRunStartAsync(JobId jobId, CancellationToken ct = default)
        {
            var run = new CronRun
            {
                Id = RunId.Create(),
                JobId = jobId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = "running"
            };
            _runs[run.Id.Value] = run;
            return Task.FromResult(run);
        }

        public Task RecordRunCompleteAsync(RunId runId, string status, string? error = null, SessionId? sessionId = null, CancellationToken ct = default)
        {
            if (_runs.TryGetValue(runId.Value, out var run))
            {
                _runs[runId.Value] = run with
                {
                    Status = status,
                    Error = error,
                    SessionId = sessionId,
                    CompletedAt = DateTimeOffset.UtcNow
                };
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CronRun>> GetRunHistoryAsync(JobId jobId, int limit = 20, CancellationToken ct = default)
        {
            var runs = _runs.Values
                .Where(run => run.JobId == jobId)
                .OrderByDescending(run => run.StartedAt)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<CronRun>>(runs);
        }

        public Task<int> PurgeRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        {
            var toRemove = _runs.Values
                .Where(r => r.CompletedAt.HasValue && r.CompletedAt.Value < cutoff && r.Status is "completed" or "failed")
                .Select(r => r.Id.Value)
                .ToList();
            foreach (var id in toRemove)
                _runs.Remove(id);
            return Task.FromResult(toRemove.Count);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
