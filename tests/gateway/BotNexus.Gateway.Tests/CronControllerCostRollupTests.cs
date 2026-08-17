using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #2641 AC5 / AC3 at the API layer: the cost rollup endpoint ranks by TOTAL spend and never
/// renders an unmeasured cost as zero.
/// </summary>
public sealed partial class CronControllerTests
{
    /// <summary>
    /// The live-data inversion, asserted through the controller: the job that is most expensive
    /// PER RUN is not the job with the highest TOTAL, and the endpoint must rank on total.
    /// </summary>
    [Fact]
    public async Task Costs_RanksByTotalSpend_NotByPerRunCost()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("daily-enrichment"));
        await store.CreateAsync(CreateJob("issue-maintenance"));

        // 2 runs at 65k each = 130k total; 8 runs at 17k each = 136k total.
        await RecordMeasuredRunsAsync(store, "daily-enrichment", runs: 2, promptTokens: 65_000);
        await RecordMeasuredRunsAsync(store, "issue-maintenance", runs: 8, promptTokens: 17_000);

        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        var result = await controller.Costs(windowDays: 7, CancellationToken.None);

        var rollups = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJobCostRollup>;
        rollups.ShouldNotBeNull();
        rollups!.Count.ShouldBe(2);

        var enrichment = rollups.Single(r => r.JobId.Value == "daily-enrichment");
        var maintenance = rollups.Single(r => r.JobId.Value == "issue-maintenance");

        enrichment.AverageTokensPerRun!.Value.ShouldBeGreaterThan(maintenance.AverageTokensPerRun!.Value);
        maintenance.TotalTokens!.Value.ShouldBeGreaterThan(enrichment.TotalTokens!.Value);
        rollups[0].JobId.Value.ShouldBe("issue-maintenance");
    }

    /// <summary>
    /// AC3 at the API layer: a job whose runs never reported tokens must surface a NULL total and
    /// a NULL average, never a 0 that would rank it as the cheapest job on the platform.
    /// </summary>
    [Fact]
    public async Task Costs_UnmeasuredJob_SurfacesNullTotal_NotZero()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("command-job", actionType: "command"));

        var run = await store.RecordRunStartAsync(JobId.From("command-job"));
        await store.RecordRunCompleteAsync(run.Id, CronRunStatus.Ok);

        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        var result = await controller.Costs(windowDays: 7, CancellationToken.None);

        var rollups = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJobCostRollup>;
        rollups.ShouldNotBeNull();
        var rollup = rollups!.Single(r => r.JobId.Value == "command-job");

        rollup.RunCount.ShouldBe(1);
        rollup.MeasuredRunCount.ShouldBe(0);
        rollup.TotalTokens.ShouldBeNull();
        rollup.TotalTokens.ShouldNotBe(0);
        rollup.AverageTokensPerRun.ShouldBeNull();
    }

    /// <summary>
    /// A measured job and an unmeasured one in the same response: the unmeasured one sorts LAST
    /// rather than being coerced to zero and sorting anywhere a real zero-cost job would.
    /// </summary>
    [Fact]
    public async Task Costs_UnmeasuredJobSortsAfterMeasuredJobs()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("measured"));
        await store.CreateAsync(CreateJob("unmeasured", actionType: "command"));

        await RecordMeasuredRunsAsync(store, "measured", runs: 1, promptTokens: 10);
        var run = await store.RecordRunStartAsync(JobId.From("unmeasured"));
        await store.RecordRunCompleteAsync(run.Id, CronRunStatus.Ok);

        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        var result = await controller.Costs(windowDays: 7, CancellationToken.None);

        var rollups = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJobCostRollup>;
        rollups.ShouldNotBeNull();
        rollups![0].JobId.Value.ShouldBe("measured");
        rollups[^1].JobId.Value.ShouldBe("unmeasured");
    }

    [Fact]
    public async Task Costs_NoJobs_ReturnsEmpty()
    {
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction(), new CronOptions());

        var result = await controller.Costs(windowDays: 7, CancellationToken.None);

        var rollups = (result.Result as OkObjectResult)?.Value as IReadOnlyList<CronJobCostRollup>;
        rollups.ShouldNotBeNull();
        rollups!.ShouldBeEmpty();
    }

    private static async Task RecordMeasuredRunsAsync(FakeCronStore store, string jobId, int runs, long promptTokens)
    {
        for (var i = 0; i < runs; i++)
        {
            var run = await store.RecordRunStartAsync(JobId.From(jobId));
            await store.RecordRunCompleteAsync(
                run.Id,
                CronRunStatus.Ok,
                cost: new CronRunCost(TurnCount: 1, ToolCallCount: 2, DurationMs: 100, PromptTokens: promptTokens));
        }
    }
}
