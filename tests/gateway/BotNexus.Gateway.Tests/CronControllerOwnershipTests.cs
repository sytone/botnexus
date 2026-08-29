using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #3575: the REST cron seam must apply the same ownership rule the tool seam applies.
/// </summary>
/// <remarks>
/// The gateway authenticates a CALLER, and a scoped caller carries an <c>AllowedAgents</c> list.
/// These tests stamp that identity into <c>HttpContext.Items</c> exactly as
/// <c>GatewayAuthMiddleware</c> does, so what is exercised is the production decision path rather
/// than a test-only shim.
/// </remarks>
public sealed partial class CronControllerTests
{
    private const string IdentityItemKey = "BotNexus.Gateway.CallerIdentity";

    [Fact]
    public async Task Update_ByCallerScopedToAnotherAgent_ReturnsForbidden()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-victim"));
        var controller = CreateScopedController(store, "agent-b");

        var result = await controller.Update(
            "job-victim",
            CreateJob("job-victim") with { Name = "Hijacked" },
            CancellationToken.None);

        var status = result.Result.ShouldBeOfType<ObjectResult>();
        status.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        // The stored definition must be untouched - a 403 that still wrote would be worse than none.
        var stored = await store.GetAsync(JobId.From("job-victim"));
        stored.ShouldNotBeNull();
        stored!.Name.ShouldBe("Test Job");
    }

    [Fact]
    public async Task Delete_ByCallerScopedToAnotherAgent_ReturnsForbiddenAndKeepsJob()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-victim"));
        var controller = CreateScopedController(store, "agent-b");

        var result = await controller.Delete("job-victim", CancellationToken.None);

        var status = result.ShouldBeOfType<ObjectResult>();
        status.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await store.GetAsync(JobId.From("job-victim"))).ShouldNotBeNull();
    }

    /// <summary>
    /// Clause 4: the <c>[FromBody] CronJob</c> bind carries AgentId and CreatedBy, and the store
    /// writes both. An unauthorized caller must not be able to capture the job through them.
    /// </summary>
    [Fact]
    public async Task Update_ByUnauthorizedCaller_CannotRewriteAgentIdOrCreatedBy()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-victim"));
        var controller = CreateScopedController(store, "agent-b");

        var capture = CreateJob("job-victim") with
        {
            AgentId = AgentId.From("agent-b"),
            CreatedBy = "agent-b"
        };

        var result = await controller.Update("job-victim", capture, CancellationToken.None);

        result.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        var stored = await store.GetAsync(JobId.From("job-victim"));
        stored.ShouldNotBeNull();
        stored!.AgentId!.Value.Value.ShouldBe("agent-a");
        stored.CreatedBy.ShouldBe("tester");
    }

    /// <summary>
    /// Clause 4, owner variant: even the legitimate owner does not author these two columns over
    /// REST. CreatedBy is server-stamped provenance, and AgentId may only move to an agent the
    /// caller is itself scoped to - here it is not, so the stored value survives a successful edit.
    /// </summary>
    [Fact]
    public async Task Update_ByOwner_CannotRetargetJobToAnAgentTheCallerIsNotScopedTo()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-owned"));
        var controller = CreateScopedController(store, "agent-a");

        var result = await controller.Update(
            "job-owned",
            CreateJob("job-owned") with
            {
                Name = "Renamed",
                AgentId = AgentId.From("agent-z"),
                CreatedBy = "agent-z"
            },
            CancellationToken.None);

        (result.Result as OkObjectResult).ShouldNotBeNull();

        var stored = await store.GetAsync(JobId.From("job-owned"));
        stored.ShouldNotBeNull();
        stored!.Name.ShouldBe("Renamed");
        stored.AgentId!.Value.Value.ShouldBe("agent-a");
        stored.CreatedBy.ShouldBe("tester");
    }

    /// <summary>Clause 5: the guard is ownership, not a blanket denial.</summary>
    [Fact]
    public async Task Update_ByTargetAgent_Succeeds()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-owned"));
        var controller = CreateScopedController(store, "agent-a");

        var result = await controller.Update(
            "job-owned",
            CreateJob("job-owned") with { Name = "Edited by owner" },
            CancellationToken.None);

        var saved = (result.Result as OkObjectResult)?.Value as CronJob;
        saved.ShouldNotBeNull();
        saved!.Name.ShouldBe("Edited by owner");
    }

    [Fact]
    public async Task Delete_ByTargetAgent_Succeeds()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-owned"));
        var controller = CreateScopedController(store, "agent-a");

        var result = await controller.Delete("job-owned", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        (await store.GetAsync(JobId.From("job-owned"))).ShouldBeNull();
    }

    /// <summary>
    /// The creator arm of the shared predicate: a caller scoped to the agent named in
    /// <c>CreatedBy</c> manages the job even when it targets someone else.
    /// </summary>
    [Fact]
    public async Task Update_ByCreatorAgent_Succeeds()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-created") with
        {
            AgentId = AgentId.From("agent-other"),
            CreatedBy = "agent-creator"
        });
        var controller = CreateScopedController(store, "agent-creator");

        var result = await controller.Update(
            "job-created",
            CreateJob("job-created") with { Name = "Edited by creator" },
            CancellationToken.None);

        (result.Result as OkObjectResult).ShouldNotBeNull();
    }

    /// <summary>
    /// An unscoped/admin caller is already trusted platform-wide by
    /// <c>GatewayAuthMiddleware.IsAgentAuthorized</c>; this guard closes the per-agent gap and must
    /// not regress that. This is also why every pre-existing CronControllerTests case, which builds
    /// a controller with no identity at all, still passes unchanged.
    /// </summary>
    [Fact]
    public async Task Update_ByAdminCaller_IsNotBlocked()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        StampIdentity(controller, new GatewayCallerIdentity
        {
            CallerId = "admin",
            AllowedAgents = ["agent-b"],
            IsAdmin = true
        });

        var result = await controller.Update(
            "job-1",
            CreateJob("job-1") with { Name = "Admin edit" },
            CancellationToken.None);

        (result.Result as OkObjectResult).ShouldNotBeNull();
    }

    /// <summary>Ownership is only reached for a job that exists; absence still answers 404.</summary>
    [Fact]
    public async Task Update_MissingJob_StillReturnsNotFound()
    {
        var store = new FakeCronStore();
        var controller = CreateScopedController(store, "agent-b");

        var result = await controller.Update(
            "no-such-job",
            CreateJob("no-such-job"),
            CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    /// <summary>
    /// #3573 / AC5: the REST seam is covered by the SAME store-level guard as the tool seam. The
    /// 403 is decided against a snapshot and an awaited alert-target validation runs before the
    /// write, so an ownership transfer landing in that window must reject the commit rather than
    /// let a caller who WAS authorized rewrite created_by/agent_id under a stale decision.
    /// </summary>
    [Fact]
    public async Task Update_WhenOwnershipChangesAfterTheAuthorizationCheck_ReturnsConflictAndKeepsOwnership()
    {
        var store = new OwnershipTransferringFakeCronStore(transferTo: "agent-thief");
        await store.CreateAsync(CreateJob("job-raced"));
        var controller = CreateScopedController(store, "agent-a");

        var result = await controller.Update(
            "job-raced",
            CreateJob("job-raced") with
            {
                Name = "Committed under a stale owner",
                AgentId = AgentId.From("agent-a"),
                CreatedBy = "agent-a"
            },
            CancellationToken.None);

        result.Result.ShouldBeOfType<ConflictObjectResult>();

        var stored = await store.GetAsync(JobId.From("job-raced"));
        stored.ShouldNotBeNull();
        stored!.CreatedBy.ShouldBe("agent-thief");
        stored.AgentId!.Value.Value.ShouldBe("agent-thief");
        stored.Name.ShouldNotBe("Committed under a stale owner");
    }

    /// <summary>
    /// Transfers ownership the first time the controller reads the job, reproducing a transfer
    /// landing inside the read-authorize-write window deterministically rather than by timing luck.
    /// </summary>
    private sealed class OwnershipTransferringFakeCronStore(string transferTo) : FakeCronStore
    {
        private int _reads;

        public override async Task<CronJob?> GetAsync(JobId jobId, CancellationToken ct = default)
        {
            var job = await base.GetAsync(jobId, ct);
            if (job is not null && Interlocked.Increment(ref _reads) == 1)
            {
                await base.UpdateDefinitionAsync(
                    job with { CreatedBy = transferTo, AgentId = AgentId.From(transferTo) },
                    expectedOwnership: null,
                    ct);
            }

            return job;
        }
    }

    private static CronController CreateScopedController(FakeCronStore store, string agentId)
    {
        var controller = CreateController(store, new RecordingAction(), new CronOptions());
        StampIdentity(controller, new GatewayCallerIdentity
        {
            CallerId = $"caller:{agentId}",
            AllowedAgents = [agentId],
            IsAdmin = false
        });
        return controller;
    }

    private static void StampIdentity(CronController controller, GatewayCallerIdentity identity)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[IdentityItemKey] = identity;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }
}
