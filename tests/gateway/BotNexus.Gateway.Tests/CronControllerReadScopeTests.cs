using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #3778: the REST cron READ seams must apply the same per-caller agent scope #3575 wired into
/// update and delete.
/// </summary>
/// <remarks>
/// #3575 hoisted the ownership rule into <see cref="CronJobOwnership"/> and called it from the two
/// write endpoints, which made the surface LOOK covered while <c>GET /api/cron</c>,
/// <c>GET /api/cron/{jobId}</c>, <c>GET /api/cron/{jobId}/runs</c> and <c>GET /api/cron/costs</c>
/// still returned every job, every run's <c>SessionId</c> and platform-wide cost data to a caller
/// scoped to a single agent. These tests pin the denied and the allowed arm of all four, because a
/// guard asserted only on its denial arm is indistinguishable from a blanket refusal.
/// </remarks>
public sealed partial class CronControllerTests
{
    /// <summary>Clause 1: a scoped caller reading another agent's job is refused.</summary>
    [Fact]
    public async Task Get_ByCallerScopedToAnotherAgent_ReturnsForbidden()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-victim"));
        var controller = CreateScopedController(store, "agent-b");

        var result = await controller.Get("job-victim", CancellationToken.None);

        var status = result.Result.ShouldBeOfType<ObjectResult>();

        // Clause 6: 403, not 404. The update path at CronController:195 states the rationale -
        // a truthful authorization answer beats an existence-oracle defence this seam does not need.
        status.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        status.Value.ShouldNotBeOfType<CronJob>();
    }

    /// <summary>Clause 5: the owner still reads its own job.</summary>
    [Fact]
    public async Task Get_ByTargetAgent_ReturnsJob()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-owned"));
        var controller = CreateScopedController(store, "agent-a");

        var result = await controller.Get("job-owned", CancellationToken.None);

        var job = (result.Result as OkObjectResult)?.Value as CronJob;
        job.ShouldNotBeNull();
        job!.Id.Value.ShouldBe("job-owned");
    }

    /// <summary>Clause 5: an admin caller is unaffected by the new guard.</summary>
    [Fact]
    public async Task Get_ByAdminCaller_ReturnsJob()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-victim"));
        var controller = CreateAdminController(store);

        var result = await controller.Get("job-victim", CancellationToken.None);

        ((result.Result as OkObjectResult)?.Value as CronJob).ShouldNotBeNull();
    }

    /// <summary>Absence still answers 404 - the guard is only reached for a job that exists.</summary>
    [Fact]
    public async Task Get_MissingJob_StillReturnsNotFound()
    {
        var store = new FakeCronStore();
        var controller = CreateScopedController(store, "agent-b");

        var result = await controller.Get("no-such-job", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    /// <summary>
    /// Clause 2: run history carries the <c>SessionId</c> that is the key into another agent's
    /// transcript, so the ownership check must precede <c>GetRunHistoryAsync</c>.
    /// </summary>
    [Fact]
    public async Task Runs_ByCallerScopedToAnotherAgent_ReturnsForbidden()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-victim"));
        await store.RecordRunStartAsync(JobId.From("job-victim"));
        var controller = CreateScopedController(store, "agent-b");

        var result = await controller.Runs("job-victim", 20, CancellationToken.None);

        var status = result.Result.ShouldBeOfType<ObjectResult>();
        status.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (status.Value as IReadOnlyList<CronRun>).ShouldBeNull();
    }

    [Fact]
    public async Task Runs_ByTargetAgent_ReturnsHistory()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-owned"));
        await store.RecordRunStartAsync(JobId.From("job-owned"));
        var controller = CreateScopedController(store, "agent-a");

        var result = await controller.Runs("job-owned", 20, CancellationToken.None);

        var runs = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronRun>;
        runs.ShouldNotBeNull();
        runs!.Count.ShouldBe(1);
    }

    /// <summary>Clause 5, runs arm.</summary>
    [Fact]
    public async Task Runs_ByAdminCaller_ReturnsHistory()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-victim"));
        await store.RecordRunStartAsync(JobId.From("job-victim"));
        var controller = CreateAdminController(store);

        var result = await controller.Runs("job-victim", 20, CancellationToken.None);

        var runs = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronRun>;
        runs.ShouldNotBeNull();
        runs!.Count.ShouldBe(1);
    }

    /// <summary>
    /// Clause 3: the list endpoint filters rather than refusing. A scoped caller sees its own jobs
    /// and nothing else - the denial arm here is an omission from the payload, not a status code.
    /// </summary>
    [Fact]
    public async Task List_ByScopedCaller_ReturnsOnlyJobsTheCallerMayManage()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-mine") with { AgentId = AgentId.From("agent-b"), CreatedBy = "agent-b" });
        await store.CreateAsync(CreateJob("job-theirs"));
        var controller = CreateScopedController(store, "agent-b");

        var result = await controller.List(CancellationToken.None);

        var jobs = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJob>;
        jobs.ShouldNotBeNull();
        jobs!.Select(job => job.Id.Value).ShouldBe(["job-mine"]);
    }

    /// <summary>
    /// The configured-job merge is the same disclosure surface: a job declared in
    /// <c>CronOptions</c> carries <c>ShellCommand</c> and <c>WebhookUrl</c> exactly as a persisted
    /// one does, so the filter must run AFTER the merge, not only over the store's rows.
    /// </summary>
    [Fact]
    public async Task List_ByScopedCaller_AlsoFiltersConfiguredJobs()
    {
        var store = new FakeCronStore();
        var options = new CronOptions
        {
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["configured-theirs"] = new()
                {
                    Name = "Configured",
                    Schedule = "*/5 * * * *",
                    ActionType = "command",
                    AgentId = "agent-a",
                    ShellCommand = "echo secret"
                }
            }
        };
        var controller = CreateController(store, new RecordingAction(), options);
        StampIdentity(controller, new GatewayCallerIdentity
        {
            CallerId = "caller:agent-b",
            AllowedAgents = ["agent-b"],
            IsAdmin = false
        });

        var result = await controller.List(CancellationToken.None);

        var jobs = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJob>;
        jobs.ShouldNotBeNull();
        jobs!.ShouldBeEmpty();
    }

    /// <summary>Clause 5, list arm: the admin tier still sees the whole platform.</summary>
    [Fact]
    public async Task List_ByAdminCaller_ReturnsEveryJob()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1"));
        await store.CreateAsync(CreateJob("job-2") with { AgentId = AgentId.From("agent-z"), CreatedBy = "agent-z" });
        var controller = CreateAdminController(store);

        var result = await controller.List(CancellationToken.None);

        var jobs = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJob>;
        jobs.ShouldNotBeNull();
        jobs!.Count.ShouldBe(2);
    }

    /// <summary>
    /// Clause 4: the cost rollup's job set is built from the ownership-filtered jobs, so a scoped
    /// caller cannot read another agent's spend.
    /// </summary>
    [Fact]
    public async Task Costs_ByScopedCaller_RollsUpOnlyJobsTheCallerMayManage()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-mine") with { AgentId = AgentId.From("agent-b"), CreatedBy = "agent-b" });
        await store.CreateAsync(CreateJob("job-theirs"));
        await RecordMeasuredRunsAsync(store, "job-mine", runs: 1, promptTokens: 10);
        await RecordMeasuredRunsAsync(store, "job-theirs", runs: 4, promptTokens: 90_000);
        var controller = CreateScopedController(store, "agent-b");

        var result = await controller.Costs(windowDays: 7, CancellationToken.None);

        var rollups = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJobCostRollup>;
        rollups.ShouldNotBeNull();
        rollups!.Select(rollup => rollup.JobId.Value).ShouldBe(["job-mine"]);
    }

    /// <summary>
    /// Clause 4's second half: when the FILTERED set is empty the endpoint short-circuits to an
    /// empty result. Without the short-circuit an empty id array reaches the store, which is the
    /// #2838 unscoped-query shape - the exact failure mode that would turn this fix into a wider
    /// disclosure than the bug it closes.
    /// </summary>
    [Fact]
    public async Task Costs_ByScopedCallerOwningNothing_ShortCircuitsWithoutQueryingTheStore()
    {
        var store = new CostQueryRecordingCronStore();
        await store.CreateAsync(CreateJob("job-theirs"));
        await RecordMeasuredRunsAsync(store, "job-theirs", runs: 2, promptTokens: 5_000);
        var controller = CreateScopedController(store, "agent-b");

        var result = await controller.Costs(windowDays: 7, CancellationToken.None);

        var rollups = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJobCostRollup>;
        rollups.ShouldNotBeNull();
        rollups!.ShouldBeEmpty();
        store.CostQueryCount.ShouldBe(0);
    }

    /// <summary>Clause 5, costs arm.</summary>
    [Fact]
    public async Task Costs_ByAdminCaller_RollsUpEveryJob()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-mine") with { AgentId = AgentId.From("agent-b"), CreatedBy = "agent-b" });
        await store.CreateAsync(CreateJob("job-theirs"));
        await RecordMeasuredRunsAsync(store, "job-mine", runs: 1, promptTokens: 10);
        await RecordMeasuredRunsAsync(store, "job-theirs", runs: 1, promptTokens: 20);
        var controller = CreateAdminController(store);

        var result = await controller.Costs(windowDays: 7, CancellationToken.None);

        var rollups = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJobCostRollup>;
        rollups.ShouldNotBeNull();
        rollups!.Count.ShouldBe(2);
    }

    /// <summary>Counts cost queries so the short-circuit can be asserted, not merely inferred.</summary>
    private sealed class CostQueryRecordingCronStore : FakeCronStore
    {
        public int CostQueryCount { get; private set; }

        public override Task<IReadOnlyList<CronJobCostRollup>> GetJobCostRollupsAsync(
            IReadOnlyCollection<JobId> jobIds,
            int windowDays,
            CancellationToken ct = default)
        {
            CostQueryCount++;
            return base.GetJobCostRollupsAsync(jobIds, windowDays, ct);
        }
    }

    private static CronController CreateAdminController(FakeCronStore store)
    {
        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        StampIdentity(controller, new GatewayCallerIdentity
        {
            CallerId = "admin",
            AllowedAgents = ["agent-nobody"],
            IsAdmin = true
        });
        return controller;
    }
}
