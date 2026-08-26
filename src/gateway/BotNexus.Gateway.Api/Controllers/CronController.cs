using BotNexus.Cron;
using BotNexus.Gateway.Api.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for cron job management and execution.
/// </summary>
/// <summary>
/// Represents cron controller.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class CronController(
    ICronStore store,
    CronScheduler scheduler,
    IOptionsMonitor<CronOptions> cronOptions,
    ILogger<CronController> logger,
    ICronAlertTargetResolver? alertTargetResolver = null) : ControllerBase
{
    // The year 9000 is chosen as a practical "absurdly far future" ceiling.
    // DateTimeOffset.MaxValue is year 9999, but any NextRunAt beyond year 9000
    // is almost certainly a client bug (e.g. a Unix millisecond timestamp passed
    // where a cron expression was expected, or overflow in a JavaScript Date calc).
    // Rejecting these early prevents them from silently polluting the scheduler's
    // run queue or causing overflow in downstream arithmetic.
    private static readonly DateTimeOffset MinAllowedTimestamp = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MaxAllowedTimestamp = new(9000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// #3575: the denial body for an unauthorized cron mutation. Emitted as an explicit 403
    /// <see cref="StatusCodeResult"/> rather than <c>Forbid()</c> because this gateway authenticates
    /// through its own middleware and registers no ASP.NET authentication scheme - <c>Forbid()</c>
    /// would throw looking for one, turning a denial into a 500.
    /// </summary>
    private const string ForbiddenMessage = "You can only manage cron jobs created by or targeting an agent you are authorized for.";

    /// <summary>Lists cron jobs.</summary>
    /// <summary>
    /// Executes list.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list result.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CronJob>>> List(CancellationToken cancellationToken)
    {
        var persisted = await store.ListAsync(ct: cancellationToken);
        var merged = persisted.ToDictionary(job => job.Id.Value, StringComparer.OrdinalIgnoreCase);
        var configuredJobs = cronOptions.CurrentValue?.Jobs;
        if (configuredJobs is not null)
        {
            foreach (var (jobId, configured) in configuredJobs)
            {
                if (merged.ContainsKey(jobId))
                    continue;

                if (string.IsNullOrWhiteSpace(jobId)
                    || string.IsNullOrWhiteSpace(configured.Schedule)
                    || string.IsNullOrWhiteSpace(configured.ActionType))
                {
                    continue;
                }

                merged[jobId] = new CronJob
                {
                    Id = JobId.From(jobId),
                    Name = configured.Name ?? jobId,
                    Schedule = configured.Schedule,
                    ActionType = NormalizeActionType(configured.ActionType),
                    AgentId = string.IsNullOrWhiteSpace(configured.AgentId) ? null : AgentId.From(configured.AgentId),
                    Message = configured.Message,
                    TemplateName = configured.TemplateName,
                    TemplateParameters = configured.TemplateParameters,
                    Model = configured.Model,
                    WebhookUrl = configured.WebhookUrl,
                    ShellCommand = configured.ShellCommand,
                    Enabled = configured.Enabled,
                    System = configured.System,
                    TimeZone = configured.TimeZone,
                    CreatedBy = configured.CreatedBy,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Metadata = configured.Metadata
                };
            }
        }

        return Ok(merged.Values.OrderByDescending(job => job.CreatedAt).ToList());
    }

    /// <summary>Gets a cron job by identifier.</summary>
    /// <summary>
    /// Executes get.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The get result.</returns>
    [HttpGet("{jobId}")]
    public async Task<ActionResult<CronJob>> Get(string jobId, CancellationToken cancellationToken)
    {
        var job = await store.GetAsync(JobId.From(jobId), cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>Creates a cron job.</summary>
    /// <summary>
    /// Executes create.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The create result.</returns>
    [HttpPost]
    public async Task<ActionResult<CronJob>> Create([FromBody] CronJobCreateRequest request, CancellationToken cancellationToken)
    {
        if (request.NextRunAt.HasValue && !IsTimestampInRange(request.NextRunAt.Value))
            return BadRequest("NextRunAt timestamp is out of the valid range (1970-01-01 to 9000-01-01).");

        if (request.CreatedAt != default && !IsTimestampInRange(request.CreatedAt))
            return BadRequest("CreatedAt timestamp is out of the valid range (1970-01-01 to 9000-01-01).");

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required.");

        if (string.IsNullOrWhiteSpace(request.Schedule))
            return BadRequest("Schedule is required.");

        // #2552: validate at the shared boundary BEFORE anything reaches the store, so a rejected
        // webhook target leaves no row behind.
        // #2745: return the rule-specific reason so the caller can tell a blocked address class
        // apart from a scheme/credentials rejection.
        if (!CronWebhookUrl.TryNormalize(request.WebhookUrl, out var normalizedWebhookUrl, out var webhookRejectionReason))
            return BadRequest(webhookRejectionReason);

        // #2671: validate the failure-alert target at the authoring seam, through the SAME shared
        // validator the update path uses, so the two seams cannot drift. This does not replace the
        // fire-time guard in the scheduler - a conversation can be deleted after the job is stored.
        var createAlertTarget = await CronAlertTarget.ValidateAsync(
            alertTargetResolver,
            string.IsNullOrWhiteSpace(request.FailureAlertConversationId)
                ? null
                : ConversationId.From(request.FailureAlertConversationId),
            cancellationToken);
        if (!createAlertTarget.IsValid)
            return BadRequest(createAlertTarget.Error);

        // #2389: the id is generated here when the caller omits one (see CronJobCreateRequest),
        // matching the existing server-side defaulting of CreatedAt and normalization of ActionType.
        var toCreate = request.ToCronJob() with
        {
            ActionType = NormalizeActionType(request.ActionType),
            WebhookUrl = normalizedWebhookUrl,
            CreatedAt = request.CreatedAt == default ? DateTimeOffset.UtcNow : request.CreatedAt
        };

        var created = await store.CreateAsync(toCreate, cancellationToken);
        logger.LogInformation("Cron job created via API: {JobId} ({ActionType})", created.Id.Value, created.ActionType);
        return CreatedAtAction(nameof(Get), new { jobId = created.Id.Value }, created);
    }

    /// <summary>Updates a cron job.</summary>
    /// <summary>
    /// Executes update.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The update result.</returns>
    [HttpPut("{jobId}")]
    public async Task<ActionResult<CronJob>> Update(string jobId, [FromBody] CronJob request, CancellationToken cancellationToken)
    {
        if (request.NextRunAt.HasValue && !IsTimestampInRange(request.NextRunAt.Value))
            return BadRequest("NextRunAt timestamp is out of the valid range (1970-01-01 to 9000-01-01).");

        // #2552: same shared boundary on the update path.
        // #2745: return the rule-specific reason so the caller can tell a blocked address class
        // apart from a scheme/credentials rejection.
        if (!CronWebhookUrl.TryNormalize(request.WebhookUrl, out var normalizedWebhookUrl, out var webhookRejectionReason))
            return BadRequest(webhookRejectionReason);

        // #2671: same shared validator on the update seam (clause 2).
        var updateAlertTarget = await CronAlertTarget.ValidateAsync(
            alertTargetResolver, request.FailureAlertConversationId, cancellationToken);
        if (!updateAlertTarget.IsValid)
            return BadRequest(updateAlertTarget.Error);

        var typedJobId = JobId.From(jobId);
        var existing = await store.GetAsync(typedJobId, cancellationToken);
        if (existing is null)
            return NotFound();

        // #3575: the REST seam applies the SAME ownership rule as the tool seam, via the shared
        // CronJobOwnership predicate. Forbidden, NOT NotFound: the caller already proved the job
        // exists by being told 404 only when it does not, and collapsing the two would trade a
        // truthful authorization answer for a existence-oracle defence this endpoint does not need.
        if (!IsCallerAuthorizedFor(existing))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ForbiddenMessage });

        var updated = request with
        {
            Id = typedJobId,
            ActionType = NormalizeActionType(request.ActionType),
            WebhookUrl = normalizedWebhookUrl,
            CreatedAt = existing.CreatedAt,

            // #3575, mirroring the #2554 shape below: PUT binds the domain record directly, so
            // AgentId and CreatedBy arrive from the request body and SqliteCronStore writes both
            // under WHERE id = $id. CreatedBy is provenance the server stamps at creation and no
            // REST caller authors it, so it is always taken from the stored row. AgentId may only
            // move to an agent the authenticated caller is itself scoped to - otherwise a single
            // body field would let an owner hand their job to an agent they cannot act as, which is
            // the ownership-capture half of this issue.
            CreatedBy = existing.CreatedBy,
            AgentId = ResolveUpdatedAgentId(request.AgentId, existing.AgentId),

            // #2554: PUT binds the domain record directly, so a caller can put a
            // ScheduleActivatedAt in the body. Strip it explicitly here (the store also refuses
            // to bind it, so this is belt-and-braces) - honouring it would let a crafted request
            // or an import spoof catch-up ownership and force an immediate agent-prompt or shell
            // execution on the next gateway start. The store re-stamps it iff Schedule/TimeZone
            // actually changed.
            ScheduleActivatedAt = existing.ScheduleActivatedAt
        };

        // #2133: a controller definition update is a narrow write that never touches
        // scheduler-owned runtime bookkeeping (LastRun*/NextRunAt) or the CAS-pinned
        // conversation. If the caller changed the schedule, recompute NextRunAt via the
        // separate narrow SetNextRunAtAsync write so a paused/racing edit cannot regress a
        // concurrent run's status, timestamps, next run, or conversation pin.
        var saved = await store.UpdateDefinitionAsync(updated, cancellationToken);
        if (saved is null)
            return NotFound();

        // #3160: disabling a job through the API must abort its in-flight run too. Gated on the
        // enabled -> disabled TRANSITION so an unrelated PUT is not a silent kill switch, and
        // routed through the same scheduler seam the tool and the delete path use.
        if (existing.Enabled && !saved.Enabled)
            await scheduler.CancelActiveRunAsync(typedJobId, cancellationToken);

        if (!string.Equals(updated.Schedule, existing.Schedule, StringComparison.Ordinal)
            || !string.Equals(updated.TimeZone ?? string.Empty, existing.TimeZone ?? string.Empty, StringComparison.Ordinal))
        {
            var nextRunAt = ComputeNextRunAt(updated.Schedule, updated.TimeZone, updated.Id);
            await store.SetNextRunAtAsync(typedJobId, nextRunAt, cancellationToken);
            saved = await store.GetAsync(typedJobId, cancellationToken) ?? saved;
        }

        logger.LogInformation("Cron job updated via API: {JobId} ({ActionType})", saved.Id.Value, saved.ActionType);
        return Ok(saved);
    }

    /// <summary>Deletes a cron job.</summary>
    /// <summary>
    /// Executes delete.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The delete result.</returns>
    [HttpDelete("{jobId}")]
    public async Task<IActionResult> Delete(string jobId, CancellationToken cancellationToken)
    {
        var typedJobId = JobId.From(jobId);

        // #3575: read before delete so the ownership rule can be applied. An absent job is still a
        // no-op NoContent (the pre-existing contract - Delete never 404'd), but a job that exists
        // and is not the caller's is a 403 rather than a silent removal of another agent's work.
        var existing = await store.GetAsync(typedJobId, cancellationToken);
        if (existing is not null && !IsCallerAuthorizedFor(existing))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ForbiddenMessage });

        // Route through the scheduler so the job's pinned conversation is archived
        // alongside the job record (P9-D directive G-5: the conversation lives until
        // the cron job is deleted).
        await scheduler.DeleteJobAsync(typedJobId, cancellationToken);
        logger.LogInformation("Cron job deleted via API: {JobId}", jobId);
        return NoContent();
    }

    /// <summary>
    /// Applies the shared <see cref="CronJobOwnership"/> rule to the authenticated caller (#3575).
    /// </summary>
    /// <remarks>
    /// The gateway authenticates a CALLER, not an agent, and a caller carries a set of permitted
    /// agent ids. An unscoped or admin caller (<c>AllowedAgents</c> empty, matching
    /// <c>GatewayAuthMiddleware.IsAgentAuthorized</c>) is already trusted platform-wide, so it stays
    /// permitted - this guard closes the per-agent gap, it is not a second authentication layer.
    /// A request with no identity in <c>HttpContext.Items</c> has not passed the auth middleware at
    /// all (unit-test construction, or an endpoint on the skip list); denying there would break
    /// callers the middleware itself allows, so the decision is deferred to it exactly as every
    /// other controller does.
    /// </remarks>
    private bool IsCallerAuthorizedFor(CronJob job)
    {
        var identity = CallerIdentity;
        if (identity is null || identity.IsAdmin || identity.AllowedAgents.Count == 0)
            return true;

        return CronJobOwnership.CanManageAsAny(job, identity.AllowedAgents);
    }

    /// <summary>
    /// Resolves the <c>AgentId</c> an update may write: the requested one when the caller is scoped
    /// to it, otherwise the stored one (#3575).
    /// </summary>
    private AgentId? ResolveUpdatedAgentId(AgentId? requested, AgentId? existing)
    {
        if (!requested.HasValue || requested == existing)
            return existing;

        var identity = CallerIdentity;
        if (identity is null || identity.IsAdmin || identity.AllowedAgents.Count == 0)
            return requested;

        var scoped = identity.AllowedAgents.Any(agent =>
            string.Equals(agent, requested.Value.Value, StringComparison.OrdinalIgnoreCase));
        return scoped ? requested : existing;
    }

    /// <summary>The identity stamped by <c>GatewayAuthMiddleware</c>, or null when unauthenticated.</summary>
    private GatewayCallerIdentity? CallerIdentity
        => HttpContext?.Items.TryGetValue(GatewayAuthMiddleware.CallerIdentityItemKey, out var value) == true
            ? value as GatewayCallerIdentity
            : null;

    /// <summary>Triggers immediate execution for a cron job.</summary>
    /// <summary>
    /// Executes run.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The run result.</returns>
    [HttpPost("{jobId}/run")]
    public async Task<ActionResult<CronRun>> Run(string jobId, CancellationToken cancellationToken)
    {
        var typedJobId = JobId.From(jobId);
        var existing = await store.GetAsync(typedJobId, cancellationToken);
        if (existing is null)
            return NotFound();

        var run = await scheduler.RunNowAsync(typedJobId, cancellationToken);
        return Accepted(run);
    }

    /// <summary>Returns cron run history for a job.</summary>
    /// <summary>
    /// Executes runs.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="limit">The limit.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The runs result.</returns>
    [HttpGet("{jobId}/runs")]
    public async Task<ActionResult<IReadOnlyList<CronRun>>> Runs(string jobId, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        var typedJobId = JobId.From(jobId);
        var existing = await store.GetAsync(typedJobId, cancellationToken);
        if (existing is null)
            return NotFound();

        return Ok(await store.GetRunHistoryAsync(typedJobId, limit, cancellationToken));
    }

    /// <summary>
    /// Per-job cost rollup over a bounded window, ordered by TOTAL spend descending (#2641).
    /// </summary>
    /// <remarks>
    /// Total, not per-run average, is the ranking that matters: a job costing a quarter as much per
    /// run but firing 24x more often is the larger consumer, and a per-run figure alone reports it
    /// as the cheaper one. The response echoes the effective window and a
    /// <c>windowTruncatedByRetention</c> flag so a caller asking for more days than run retention
    /// holds learns the total is bounded rather than silently reading a truncated number as a
    /// complete one.
    /// </remarks>
    /// <param name="windowDays">Requested window in days; clamped to the configured run retention.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Cost rollups, most expensive total first.</returns>
    [HttpGet("costs")]
    public async Task<ActionResult<IReadOnlyList<CronJobCostRollup>>> Costs(
        [FromQuery] int windowDays = 7,
        CancellationToken cancellationToken = default)
    {
        var jobs = await store.ListAsync(ct: cancellationToken);
        var jobIds = jobs.Select(job => job.Id).ToArray();

        // An empty job set short-circuits to an empty result rather than an unscoped query - the
        // #2838 rule, restated here because this controller builds the scope itself.
        if (jobIds.Length == 0)
            return Ok(Array.Empty<CronJobCostRollup>());

        return Ok(await store.GetJobCostRollupsAsync(jobIds, windowDays, cancellationToken));
    }

    private static string NormalizeActionType(string? actionType)
    {
        if (string.Equals(actionType, "agent-chat", StringComparison.OrdinalIgnoreCase))
            return "agent-prompt";

        return actionType?.Trim() ?? string.Empty;
    }

    private static bool IsTimestampInRange(DateTimeOffset value)
        => value >= MinAllowedTimestamp && value <= MaxAllowedTimestamp;

    // #2133: recompute NextRunAt for a schedule/timezone change on the definition-update path.
    // #2810: the computation itself, INCLUDING its DST-transition policy, is delegated to
    // CronExpressionExtensions so this path cannot drift from the scheduler that will actually fire
    // the job. A bad schedule yields null - the scheduler's Phase-1 tick re-derives NextRunAt on
    // the next pass.
    private DateTimeOffset? ComputeNextRunAt(string schedule, string? timeZone, JobId jobId)
    {
        try
        {
            // #2748/#2810: the canonical resolver, not a local copy. This controller previously
            // carried its own FindSystemTimeZoneById cascade, which is the same duplication #2748
            // removed from the scheduler and the tool - and being outside BotNexus.Cron, it was
            // invisible to that issue's single-definition fence.
            var tz = CronTimeZoneResolver.Resolve(timeZone, logger, jobId);
            var expr = Cronos.CronExpression.Parse(schedule, Cronos.CronFormat.Standard);
            return expr.NextRun(DateTimeOffset.UtcNow, tz);
        }
        catch
        {
            return null;
        }
    }
}
