using System.Text.Json;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #3338 clauses 6-8 and 10: the self-pacing <c>next_check</c> bound, at both the pure-decision layer
/// and the tool seam.
/// </summary>
/// <remarks>
/// Every clamp assertion here checks the EFFECTIVE value, not merely that the call succeeded. A test
/// that only asserts success would pass against a clamp that silently ignored the request entirely,
/// which is precisely the failure mode clause 10 calls out.
/// </remarks>
public sealed class CronSelfPacingBoundTests
{
    // ---- Decision layer -----------------------------------------------------------------

    /// <summary>Happy path: a request inside the bound is honoured verbatim and reported unclamped.</summary>
    [Fact]
    public void Clamp_WithinBound_HonoursRequestVerbatim()
    {
        var decision = CronSelfPacingBound.Clamp(TimeSpan.FromMinutes(15));

        decision.Effective.ShouldBe(TimeSpan.FromMinutes(15));
        decision.Requested.ShouldBe(TimeSpan.FromMinutes(15));
        decision.WasClamped.ShouldBeFalse();
        decision.Reason.ShouldBe(CronSelfPacingBound.ClampReason.None);
    }

    /// <summary>Sad path (floor): a too-eager request is RAISED, and the raise is visible.</summary>
    [Fact]
    public void Clamp_BelowFloor_RaisesToFloorAndReportsIt()
    {
        var decision = CronSelfPacingBound.Clamp(TimeSpan.FromSeconds(5));

        decision.Effective.ShouldBe(CronSelfPacingBound.DefaultFloor);
        decision.Requested.ShouldBe(TimeSpan.FromSeconds(5));
        decision.WasClamped.ShouldBeTrue();
        decision.Reason.ShouldBe(CronSelfPacingBound.ClampReason.Floor);
    }

    /// <summary>Sad path (ceiling): a request that would park the loop far out is LOWERED, visibly.</summary>
    [Fact]
    public void Clamp_AboveCeiling_LowersToCeilingAndReportsIt()
    {
        var decision = CronSelfPacingBound.Clamp(TimeSpan.FromDays(30));

        decision.Effective.ShouldBe(CronSelfPacingBound.DefaultCeiling);
        decision.Requested.ShouldBe(TimeSpan.FromDays(30));
        decision.WasClamped.ShouldBeTrue();
        decision.Reason.ShouldBe(CronSelfPacingBound.ClampReason.Ceiling);
    }

    /// <summary>
    /// The two clamped directions must not share a symbol. "Pinned low" (burning turns) and
    /// "pinned high" (silently idle) are opposite operational problems.
    /// </summary>
    [Fact]
    public void Clamp_FloorAndCeiling_AreDistinctReasons()
        => CronSelfPacingBound.Clamp(TimeSpan.Zero).Reason
            .ShouldNotBe(CronSelfPacingBound.Clamp(TimeSpan.FromDays(1)).Reason);

    /// <summary>A configured bound is honoured, not just the default one.</summary>
    [Fact]
    public void Clamp_HonoursConfiguredBounds()
    {
        var decision = CronSelfPacingBound.Clamp(
            TimeSpan.FromSeconds(30),
            floor: TimeSpan.FromSeconds(10),
            ceiling: TimeSpan.FromSeconds(20));

        decision.Effective.ShouldBe(TimeSpan.FromSeconds(20));
        decision.Reason.ShouldBe(CronSelfPacingBound.ClampReason.Ceiling);
    }

    /// <summary>
    /// Misconfiguration degrades to the DEFAULT bound, never to no bound. A zero/negative floor that
    /// disabled the clamp would hand back exactly the runaway surface the clamp exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Clamp_NonPositiveConfiguredFloor_FallsBackToDefaultFloor_NotToNoFloor(int floorSeconds)
    {
        var decision = CronSelfPacingBound.Clamp(TimeSpan.Zero, floor: TimeSpan.FromSeconds(floorSeconds));

        decision.Effective.ShouldBe(CronSelfPacingBound.DefaultFloor);
        decision.WasClamped.ShouldBeTrue();
    }

    /// <summary>An inverted configuration cannot produce a ceiling below the floor.</summary>
    [Fact]
    public void Clamp_CeilingBelowFloor_DegradesRatherThanInverting()
    {
        var decision = CronSelfPacingBound.Clamp(
            TimeSpan.FromHours(10),
            floor: TimeSpan.FromMinutes(5),
            ceiling: TimeSpan.FromMinutes(1));

        decision.Ceiling.ShouldBeGreaterThanOrEqualTo(decision.Floor);
        decision.Effective.ShouldBeGreaterThanOrEqualTo(decision.Floor);
    }

    /// <summary>A negative proposal is a floor clamp, not a wake instant in the past.</summary>
    [Fact]
    public void Clamp_NegativeRequest_RaisesToFloor()
        => CronSelfPacingBound.Clamp(TimeSpan.FromSeconds(-600)).Effective
            .ShouldBe(CronSelfPacingBound.DefaultFloor);

    // ---- Tool seam ----------------------------------------------------------------------

    /// <summary>Schema half: the model cannot invoke an action the schema never declares.</summary>
    [Fact]
    public void Definition_DeclaresNextCheckActionAndSeconds()
    {
        var tool = CronToolFailureAlertSurfaceTests.CreateTool(new Moq.Mock<ICronStore>().Object);
        var properties = tool.Definition.Parameters.GetProperty("properties");

        properties.GetProperty("action").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).ShouldContain("next_check");
        properties.TryGetProperty("nextCheckSeconds", out var seconds).ShouldBeTrue();
        seconds.GetProperty("type").GetString().ShouldBe("integer");
    }

    /// <summary>
    /// #2641 allow-list trap: <c>nextCheckSeconds</c> must survive <c>PrepareArgumentsAsync</c>.
    /// An argument the schema declares but the allow-list forgets is silently dropped and the handler
    /// reads its default - the invisible failure this asserts against by name.
    /// </summary>
    [Fact]
    public async Task PrepareArguments_CopiesNextCheckSecondsThrough()
    {
        var tool = CronToolFailureAlertSurfaceTests.CreateTool(new Moq.Mock<ICronStore>().Object);

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["action"] = "next_check",
            ["jobId"] = "job-a",
            ["nextCheckSeconds"] = 900
        });

        prepared.ShouldContainKey("nextCheckSeconds");
        prepared["nextCheckSeconds"].ShouldBe(900);
    }

    /// <summary>
    /// Clause 7 happy path: an in-bound proposal is applied and the response states BOTH values.
    /// The effective delay is asserted, not merely the call's success.
    /// </summary>
    [Fact]
    public async Task NextCheck_WithinBound_AppliesRequestedDelayAndReportsBothValues()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("loop-job", "agent-a"));
        var before = DateTimeOffset.UtcNow;

        var response = await InvokeNextCheckAsync(context, "loop-job", 900);

        response.GetProperty("requestedSeconds").GetInt32().ShouldBe(900);
        response.GetProperty("effectiveSeconds").GetInt32().ShouldBe(900);
        response.GetProperty("wasClamped").GetBoolean().ShouldBeFalse();

        var job = await context.Store.GetAsync(JobId.From("loop-job"));
        job!.BackoffUntil.ShouldNotBeNull();
        job.BackoffUntil!.Value.ShouldBeGreaterThan(before.AddSeconds(880));
        job.BackoffUntil!.Value.ShouldBeLessThan(before.AddSeconds(960));
    }

    /// <summary>
    /// Clause 7 sad path: a sub-floor proposal is clamped AND the clamp is observable. The requested
    /// value is still reported, so a loop pinned at the floor cannot masquerade as one pacing itself.
    /// </summary>
    [Fact]
    public async Task NextCheck_BelowFloor_IsClampedAndTheClampIsObservable()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("eager-job", "agent-a"));

        var response = await InvokeNextCheckAsync(context, "eager-job", 1);

        response.GetProperty("requestedSeconds").GetInt32().ShouldBe(1);
        response.GetProperty("effectiveSeconds").GetInt32().ShouldBe(60);
        response.GetProperty("wasClamped").GetBoolean().ShouldBeTrue();
        response.GetProperty("clampReason").GetString().ShouldBe("Floor");
        response.GetProperty("floorSeconds").GetInt32().ShouldBe(60);
        response.GetProperty("ceilingSeconds").GetInt32().ShouldBe(3600);
    }

    /// <summary>Clause 7 sad path, the other direction: a week-long proposal is pinned to the ceiling.</summary>
    [Fact]
    public async Task NextCheck_AboveCeiling_IsClampedToCeiling()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("idle-job", "agent-a"));

        var response = await InvokeNextCheckAsync(context, "idle-job", 604_800);

        response.GetProperty("effectiveSeconds").GetInt32().ShouldBe(3600);
        response.GetProperty("clampReason").GetString().ShouldBe("Ceiling");

        var job = await context.Store.GetAsync(JobId.From("idle-job"));
        job!.BackoffUntil!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow.AddSeconds(3700));
    }

    /// <summary>Configured bounds reach the tool rather than the tool hard-coding the defaults.</summary>
    [Fact]
    public async Task NextCheck_HonoursConfiguredBounds()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("tight-job", "agent-a"));

        var response = await InvokeNextCheckAsync(
            context,
            "tight-job",
            900,
            new CronOptions { SelfPacingFloorSeconds = 30, SelfPacingCeilingSeconds = 120 });

        response.GetProperty("effectiveSeconds").GetInt32().ShouldBe(120);
        response.GetProperty("ceilingSeconds").GetInt32().ShouldBe(120);
        response.GetProperty("wasClamped").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// Clause 8: scope follows the SAME CanManage rule as history/costs. Another agent's job is
    /// refused, not re-paced - otherwise any agent could stall every other agent's loops.
    /// </summary>
    [Fact]
    public async Task NextCheck_ForAnotherAgentsJob_IsRefused()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("their-job", "agent-b"));

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => InvokeNextCheckAsync(context, "their-job", 300));

        // and the refusal left no pacing behind.
        var job = await context.Store.GetAsync(JobId.From("their-job"));
        job!.BackoffUntil.ShouldBeNull();
    }

    /// <summary>A next_check against a job that does not exist is a miss, not a silent no-op.</summary>
    [Fact]
    public async Task NextCheck_ForUnknownJob_Throws()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        await Should.ThrowAsync<KeyNotFoundException>(
            () => InvokeNextCheckAsync(context, "no-such-job", 300));
    }

    /// <summary>
    /// An omitted proposal is REJECTED rather than defaulted. "Propose nothing" silently becoming
    /// "propose the floor" would write a pacing decision the agent never made.
    /// </summary>
    [Fact]
    public async Task NextCheck_WithoutSeconds_IsRejected()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("loop-job", "agent-a"));
        var tool = CreateTool(context);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await CronToolFailureAlertSurfaceTests.InvokeAsync(tool, new Dictionary<string, object?>
            {
                ["action"] = "next_check",
                ["jobId"] = "loop-job"
            }));

        var job = await context.Store.GetAsync(JobId.From("loop-job"));
        job!.BackoffUntil.ShouldBeNull();
    }

    /// <summary>next_check must not be able to move a job's NextRunAt: those are two different facts (#3350).</summary>
    [Fact]
    public async Task NextCheck_WritesBackoffOnly_NotNextRunAt()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("loop-job", "agent-a"));
        await context.Store.SetNextRunAtAsync(JobId.From("loop-job"), DateTimeOffset.UtcNow.AddMinutes(5));
        var pinned = (await context.Store.GetAsync(JobId.From("loop-job")))!.NextRunAt;

        await InvokeNextCheckAsync(context, "loop-job", 600);

        var job = await context.Store.GetAsync(JobId.From("loop-job"));
        job!.NextRunAt.ShouldBe(pinned);
        job.BackoffUntil.ShouldNotBeNull();
    }

    private static CronTool CreateTool(CronStoreTestContext context, CronOptions? options = null)
        => new(
            context.Store,
            CronToolFailureAlertSurfaceTests.CreateScheduler(context.Store, []),
            AgentId.From("agent-a"),
            allowCrossAgentCron: false,
            alertTargetResolver: new CronToolFailureAlertSurfaceTests.StubResolver(exists: true),
            cronOptions: options);

    private static Task<JsonElement> InvokeNextCheckAsync(
        CronStoreTestContext context,
        string jobId,
        int seconds,
        CronOptions? options = null)
        => CronToolFailureAlertSurfaceTests.InvokeAsync(
            CreateTool(context, options),
            new Dictionary<string, object?>
            {
                ["action"] = "next_check",
                ["jobId"] = jobId,
                ["nextCheckSeconds"] = seconds
            });
}
