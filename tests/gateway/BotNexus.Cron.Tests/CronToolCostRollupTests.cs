using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2641 at the tool seam: <c>cron action=costs</c> answers "which of my jobs cost the most", and
/// answers it by TOTAL rather than by per-run average.
/// </summary>
public sealed class CronToolCostRollupTests
{
    /// <summary>
    /// AC5, through the tool: the fixture is the live-data inversion. The job that is most
    /// expensive per run is NOT the highest-total job, and the ranking must follow total.
    /// </summary>
    [Fact]
    public async Task Costs_RanksByTotal_NotByPerRunCost()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await SeedMeasuredRunsAsync(context, "daily-enrichment", "agent-a", runs: 2, promptTokens: 65_000);
        await SeedMeasuredRunsAsync(context, "issue-maintenance", "agent-a", runs: 8, promptTokens: 17_000);

        var tool = CreateScopedTool(context);
        var rollups = await CronToolFailureAlertSurfaceTests.InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "costs",
            ["windowDays"] = 7
        });

        var ordered = rollups.EnumerateArray().ToList();
        ordered.Count.ShouldBe(2);
        ordered[0].GetProperty("jobId").GetString().ShouldBe("issue-maintenance");
        ordered[0].GetProperty("totalTokens").GetInt64().ShouldBe(136_000);
        ordered[1].GetProperty("jobId").GetString().ShouldBe("daily-enrichment");
        ordered[1].GetProperty("totalTokens").GetInt64().ShouldBe(130_000);

        // ...while the per-run figure ranks them the other way round.
        ordered[0].GetProperty("averageTokensPerRun").GetDouble().ShouldBe(17_000);
        ordered[1].GetProperty("averageTokensPerRun").GetDouble().ShouldBe(65_000);
    }

    /// <summary>
    /// AC3 at the tool layer: an unmeasured job serialises a JSON <c>null</c>, not a <c>0</c>.
    /// A model reading a 0 here would confidently report the job as free.
    /// </summary>
    [Fact]
    public async Task Costs_UnmeasuredJob_SerialisesNullTotal_NotZero()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("command-job", "agent-a", actionType: "command"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("command-job"));
        await context.Store.RecordRunCompleteAsync(run.Id, CronRunStatus.Ok);

        var tool = CreateScopedTool(context);
        var rollups = await CronToolFailureAlertSurfaceTests.InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "costs"
        });

        var entry = rollups.EnumerateArray().ShouldHaveSingleItem();
        entry.GetProperty("runCount").GetInt32().ShouldBe(1);
        entry.GetProperty("measuredRunCount").GetInt32().ShouldBe(0);
        entry.GetProperty("totalTokens").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        entry.GetProperty("averageTokensPerRun").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    /// <summary>
    /// Scope follows the same CanManage rule as history: an agent with no manageable jobs gets an
    /// empty result, never every job's costs.
    /// </summary>
    [Fact]
    public async Task Costs_ForAgentWithNoManageableJobs_ReturnsEmpty()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await SeedMeasuredRunsAsync(context, "job-theirs", "agent-b", runs: 1, promptTokens: 1_000);

        var tool = CreateScopedTool(context);
        var rollups = await CronToolFailureAlertSurfaceTests.InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "costs"
        });

        rollups.EnumerateArray().ShouldBeEmpty();
    }

    /// <summary>AC6 surfaced to the model: the truncation flag rides every entry.</summary>
    [Fact]
    public async Task Costs_ReportsRetentionTruncationFlag()
    {
        await using var context = await CronStoreTestContext.CreateAsync(retentionDays: 30);
        await SeedMeasuredRunsAsync(context, "job-window", "agent-a", runs: 1, promptTokens: 10);

        var tool = CreateScopedTool(context);
        var rollups = await CronToolFailureAlertSurfaceTests.InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "costs",
            ["windowDays"] = 90
        });

        var entry = rollups.EnumerateArray().ShouldHaveSingleItem();
        entry.GetProperty("windowDays").GetInt32().ShouldBe(30);
        entry.GetProperty("windowTruncatedByRetention").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// Schema half: the model cannot invoke an action the schema never declares, so 'costs' must
    /// appear in the enum alongside its windowDays parameter.
    /// </summary>
    [Fact]
    public void Definition_DeclaresCostsActionAndWindowDays()
    {
        var tool = CronToolFailureAlertSurfaceTests.CreateTool(new Mock<ICronStore>().Object);
        var properties = tool.Definition.Parameters.GetProperty("properties");

        var actions = properties.GetProperty("action").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).ToList();
        actions.ShouldContain("costs");

        properties.TryGetProperty("windowDays", out var windowDays).ShouldBeTrue();
        windowDays.GetProperty("type").GetString().ShouldBe("integer");
    }

    private static CronTool CreateScopedTool(CronStoreTestContext context)
        => new(
            context.Store,
            CronToolFailureAlertSurfaceTests.CreateScheduler(context.Store, []),
            AgentId.From("agent-a"),
            allowCrossAgentCron: false,
            alertTargetResolver: new CronToolFailureAlertSurfaceTests.StubResolver(exists: true));

    private static async Task SeedMeasuredRunsAsync(
        CronStoreTestContext context,
        string jobId,
        string agentId,
        int runs,
        long promptTokens)
    {
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob(jobId, agentId));
        for (var i = 0; i < runs; i++)
        {
            var run = await context.Store.RecordRunStartAsync(JobId.From(jobId));
            await context.Store.RecordRunCompleteAsync(
                run.Id,
                CronRunStatus.Ok,
                cost: new CronRunCost(TurnCount: 1, ToolCallCount: 2, DurationMs: 100, PromptTokens: promptTokens));
        }
    }
}
