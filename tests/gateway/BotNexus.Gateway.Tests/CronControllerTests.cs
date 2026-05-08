using BotNexus.Cron;
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
        var controller = CreateController(store, new RecordingAction());

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
        var controller = CreateController(store, new RecordingAction());

        var result = await controller.Get("job-1", CancellationToken.None);

        var job = (result.Result as OkObjectResult)?.Value as CronJob;
        job.ShouldNotBeNull();
        job!.Id.ShouldBe("job-1");
    }

    [Fact]
    public async Task Create_ReturnsCreated()
    {
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction());
        var request = CreateJob(string.Empty) with { Id = string.Empty };

        var result = await controller.Create(request, CancellationToken.None);

        var created = (result.Result as CreatedAtActionResult)?.Value as CronJob;
        created.ShouldNotBeNull();
        created!.Id.ShouldNotBeNullOrWhiteSpace();
        (await store.GetAsync(created.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Delete_ReturnsNoContent()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1"));
        var controller = CreateController(store, new RecordingAction());

        var result = await controller.Delete("job-1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        (await store.GetAsync("job-1")).ShouldBeNull();
    }

    [Fact]
    public async Task RunNow_TriggersExecution()
    {
        var action = new RecordingAction();
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1", actionType: action.ActionType));
        var controller = CreateController(store, action);

        var result = await controller.Run("job-1", CancellationToken.None);

        var run = (result.Result as AcceptedResult)?.Value as CronRun;
        run.ShouldNotBeNull();
        run!.Status.ShouldBe("ok");
        action.ExecutionCount.ShouldBe(1);
    }

    private static CronController CreateController(FakeCronStore store, ICronAction action)
    {
        var scheduler = new CronScheduler(
            store,
            [action],
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions()),
            NullLogger<CronScheduler>.Instance);
        return new CronController(store, scheduler);
    }

    private static CronJob CreateJob(string id, string actionType = "agent-prompt")
        => new()
        {
            Id = id,
            Name = "Test Job",
            Schedule = "*/1 * * * *",
            ActionType = actionType,
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
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
                Id = string.IsNullOrWhiteSpace(job.Id) ? Guid.NewGuid().ToString("N") : job.Id,
                CreatedAt = job.CreatedAt == default ? DateTimeOffset.UtcNow : job.CreatedAt
            };
            _jobs[created.Id] = created;
            return Task.FromResult(created);
        }

        public Task<CronJob?> GetAsync(string jobId, CancellationToken ct = default)
            => Task.FromResult(_jobs.GetValueOrDefault(jobId));

        public Task<IReadOnlyList<CronJob>> ListAsync(string? agentId = null, CancellationToken ct = default)
        {
            IReadOnlyList<CronJob> jobs = agentId is null
                ? [.. _jobs.Values]
                : _jobs.Values.Where(job => string.Equals(job.AgentId, agentId, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult(jobs);
        }

        public Task<CronJob> UpdateAsync(CronJob job, CancellationToken ct = default)
        {
            _jobs[job.Id] = job;
            return Task.FromResult(job);
        }

        public Task DeleteAsync(string jobId, CancellationToken ct = default)
        {
            _jobs.Remove(jobId);
            foreach (var runId in _runs.Values.Where(run => run.JobId == jobId).Select(run => run.Id).ToList())
                _runs.Remove(runId);
            return Task.CompletedTask;
        }

        public Task<CronRun> RecordRunStartAsync(string jobId, CancellationToken ct = default)
        {
            var run = new CronRun
            {
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = "running"
            };
            _runs[run.Id] = run;
            return Task.FromResult(run);
        }

        public Task RecordRunCompleteAsync(string runId, string status, string? error = null, string? sessionId = null, CancellationToken ct = default)
        {
            if (_runs.TryGetValue(runId, out var run))
            {
                _runs[runId] = run with
                {
                    Status = status,
                    Error = error,
                    SessionId = sessionId,
                    CompletedAt = DateTimeOffset.UtcNow
                };
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CronRun>> GetRunHistoryAsync(string jobId, int limit = 20, CancellationToken ct = default)
        {
            var runs = _runs.Values
                .Where(run => run.JobId == jobId)
                .OrderByDescending(run => run.StartedAt)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<CronRun>>(runs);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
