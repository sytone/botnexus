using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2838 (second half): <c>cron action=history</c> required a <c>jobId</c>, so "which of my jobs
/// have failed recently" cost N calls and was only ever asked after a human noticed something was
/// missing. The #2819 hijack ran for ~2 days across at least 4 jobs and was found by manual
/// per-job polling. These tests pin the cross-job query at both the store and the tool seam.
/// </summary>
public sealed class CronToolCrossJobHistoryTests
{
    /// <summary>
    /// AC6: history with no jobId returns runs across jobs, newest first, scoped to jobs the
    /// caller may manage. The foreign job's run must be absent - a cross-job view that ignores
    /// authorisation would be a worse defect than the one being fixed.
    /// </summary>
    [Fact]
    public async Task History_WithoutJobId_ReturnsRunsAcrossManageableJobsOnly()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await SeedRunAsync(context, "job-mine-1", "agent-a", CronRunStatus.Ok);
        await SeedRunAsync(context, "job-mine-2", "agent-a", CronRunStatus.Error);
        await SeedRunAsync(context, "job-theirs", "agent-b", CronRunStatus.Error);

        var tool = CreateScopedTool(context);
        var runs = await CronToolFailureAlertSurfaceTests.InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "history"
        });

        var jobIds = runs.EnumerateArray()
            .Select(run => run.GetProperty("jobId").GetString())
            .ToList();

        jobIds.ShouldContain("job-mine-1");
        jobIds.ShouldContain("job-mine-2");
        jobIds.ShouldNotContain("job-theirs");
    }

    /// <summary>AC6 (filter half): the cross-job view can be narrowed to failed runs only.</summary>
    [Fact]
    public async Task History_WithoutJobId_FiltersToFailedRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await SeedRunAsync(context, "job-ok", "agent-a", CronRunStatus.Ok);
        await SeedRunAsync(context, "job-bad", "agent-a", CronRunStatus.Error);
        await SeedRunAsync(context, "job-silent", "agent-a", CronRunStatus.NoToolCalls);

        var tool = CreateScopedTool(context);
        var runs = await CronToolFailureAlertSurfaceTests.InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "history",
            ["failedOnly"] = true
        });

        var statuses = runs.EnumerateArray()
            .Select(run => run.GetProperty("status").GetString())
            .ToList();

        statuses.ShouldNotBeEmpty();
        statuses.ShouldNotContain(CronRunStatus.Ok);
        statuses.ShouldContain(CronRunStatus.Error);
        // #2985's terminal non-success counts as a failure for the operator asking "what broke".
        statuses.ShouldContain(CronRunStatus.NoToolCalls);
    }

    /// <summary>
    /// The per-job path is unchanged: supplying a jobId still scopes to that job alone, so the new
    /// optional-jobId behaviour is additive rather than a silent widening.
    /// </summary>
    [Fact]
    public async Task History_WithJobId_StillScopesToThatJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await SeedRunAsync(context, "job-one", "agent-a", CronRunStatus.Ok);
        await SeedRunAsync(context, "job-two", "agent-a", CronRunStatus.Error);

        var tool = CreateScopedTool(context);
        var runs = await CronToolFailureAlertSurfaceTests.InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "history",
            ["jobId"] = "job-one"
        });

        var run = runs.EnumerateArray().ShouldHaveSingleItem();
        run.GetProperty("jobId").GetString().ShouldBe("job-one");
    }

    /// <summary>
    /// Sad path: an agent with no manageable jobs gets an empty result rather than every job's
    /// history. A cross-job query whose scope collapses to "everything" is the failure mode worth
    /// pinning by name.
    /// </summary>
    [Fact]
    public async Task History_WithoutJobId_ForAgentWithNoJobs_ReturnsEmpty()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await SeedRunAsync(context, "job-theirs", "agent-b", CronRunStatus.Error);

        var tool = CreateScopedTool(context);
        var runs = await CronToolFailureAlertSurfaceTests.InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "history"
        });

        runs.EnumerateArray().ShouldBeEmpty();
    }

    /// <summary>Store level: the cross-job query honours its job-id scope and its status filter.</summary>
    [Fact]
    public async Task Store_GetRecentRunsAsync_ScopesByJobIdsAndStatus()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await SeedRunAsync(context, "job-a", "agent-a", CronRunStatus.Error);
        await SeedRunAsync(context, "job-b", "agent-a", CronRunStatus.Ok);
        await SeedRunAsync(context, "job-c", "agent-b", CronRunStatus.Error);

        var scoped = await context.Store.GetRecentRunsAsync(
            [JobId.From("job-a"), JobId.From("job-b")],
            statuses: null,
            limit: 20);
        scoped.Select(run => run.JobId.Value).ShouldBe(["job-a", "job-b"], ignoreOrder: true);

        var failed = await context.Store.GetRecentRunsAsync(
            [JobId.From("job-a"), JobId.From("job-b")],
            statuses: [CronRunStatus.Error],
            limit: 20);
        failed.ShouldHaveSingleItem().JobId.Value.ShouldBe("job-a");
    }

    /// <summary>
    /// An EMPTY scope means "no jobs", never "no filter". SQL <c>IN ()</c> is the classic place
    /// this inverts into returning everything, so it is asserted separately from the null case.
    /// </summary>
    [Fact]
    public async Task Store_GetRecentRunsAsync_WithEmptyScope_ReturnsNothing()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await SeedRunAsync(context, "job-a", "agent-a", CronRunStatus.Error);

        var scoped = await context.Store.GetRecentRunsAsync([], statuses: null, limit: 20);

        scoped.ShouldBeEmpty();
    }

    /// <summary>AC6 schema half: the model cannot ask for a filter the schema never declares.</summary>
    [Fact]
    public void Definition_DeclaresFailedOnlyAndOptionalJobId()
    {
        var tool = CronToolFailureAlertSurfaceTests.CreateTool(new Mock<ICronStore>().Object);
        var properties = tool.Definition.Parameters.GetProperty("properties");

        properties.TryGetProperty("failedOnly", out var failedOnly).ShouldBeTrue();
        failedOnly.GetProperty("type").GetString().ShouldBe("boolean");

        // jobId must not be declared required for history; the schema's only required key is action.
        var required = tool.Definition.Parameters.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()).ToList();
        required.ShouldBe(["action"]);
    }

    // --- helpers ---

    private static CronTool CreateScopedTool(CronStoreTestContext context)
        => new(
            context.Store,
            CronToolFailureAlertSurfaceTests.CreateScheduler(context.Store, []),
            AgentId.From("agent-a"),
            allowCrossAgentCron: false,
            alertTargetResolver: new CronToolFailureAlertSurfaceTests.StubResolver(exists: true));

    private static async Task SeedRunAsync(
        CronStoreTestContext context,
        string jobId,
        string agentId,
        string status)
    {
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob(jobId, agentId));
        var run = await context.Store.RecordRunStartAsync(JobId.From(jobId));
        await context.Store.RecordRunCompleteAsync(run.Id, status, status == CronRunStatus.Ok ? null : "boom");
        // Distinct started_at instants so "newest first" is well-defined.
        await Task.Delay(5);
    }
}
