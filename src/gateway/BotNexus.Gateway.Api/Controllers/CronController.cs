using BotNexus.Cron;
using BotNexus.Domain.Primitives;
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
    ILogger<CronController> logger) : ControllerBase
{
    // The year 9000 is chosen as a practical "absurdly far future" ceiling.
    // DateTimeOffset.MaxValue is year 9999, but any NextRunAt beyond year 9000
    // is almost certainly a client bug (e.g. a Unix millisecond timestamp passed
    // where a cron expression was expected, or overflow in a JavaScript Date calc).
    // Rejecting these early prevents them from silently polluting the scheduler's
    // run queue or causing overflow in downstream arithmetic.
    private static readonly DateTimeOffset MinAllowedTimestamp = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MaxAllowedTimestamp = new(9000, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
    public async Task<ActionResult<CronJob>> Create([FromBody] CronJob request, CancellationToken cancellationToken)
    {
        if (request.NextRunAt.HasValue && !IsTimestampInRange(request.NextRunAt.Value))
            return BadRequest("NextRunAt timestamp is out of the valid range (1970-01-01 to 9000-01-01).");

        if (request.CreatedAt != default && !IsTimestampInRange(request.CreatedAt))
            return BadRequest("CreatedAt timestamp is out of the valid range (1970-01-01 to 9000-01-01).");

        var toCreate = request with
        {
            ActionType = NormalizeActionType(request.ActionType),
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

        var typedJobId = JobId.From(jobId);
        var existing = await store.GetAsync(typedJobId, cancellationToken);
        if (existing is null)
            return NotFound();

        var updated = request with
        {
            Id = typedJobId,
            ActionType = NormalizeActionType(request.ActionType),
            CreatedAt = existing.CreatedAt
        };

        // #2133: a controller definition update is a narrow write that never touches
        // scheduler-owned runtime bookkeeping (LastRun*/NextRunAt) or the CAS-pinned
        // conversation. If the caller changed the schedule, recompute NextRunAt via the
        // separate narrow SetNextRunAtAsync write so a paused/racing edit cannot regress a
        // concurrent run's status, timestamps, next run, or conversation pin.
        var saved = await store.UpdateDefinitionAsync(updated, cancellationToken);
        if (saved is null)
            return NotFound();

        if (!string.Equals(updated.Schedule, existing.Schedule, StringComparison.Ordinal)
            || !string.Equals(updated.TimeZone ?? string.Empty, existing.TimeZone ?? string.Empty, StringComparison.Ordinal))
        {
            var nextRunAt = ComputeNextRunAt(updated.Schedule, updated.TimeZone);
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
        // Route through the scheduler so the job's pinned conversation is archived
        // alongside the job record (P9-D directive G-5: the conversation lives until
        // the cron job is deleted).
        await scheduler.DeleteJobAsync(JobId.From(jobId), cancellationToken);
        logger.LogInformation("Cron job deleted via API: {JobId}", jobId);
        return NoContent();
    }

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

    private static string NormalizeActionType(string? actionType)
    {
        if (string.Equals(actionType, "agent-chat", StringComparison.OrdinalIgnoreCase))
            return "agent-prompt";

        return actionType?.Trim() ?? string.Empty;
    }

    private static bool IsTimestampInRange(DateTimeOffset value)
        => value >= MinAllowedTimestamp && value <= MaxAllowedTimestamp;

    // #2133: recompute NextRunAt for a schedule/timezone change on the definition-update path.
    // Mirrors the scheduler/tool computation (Cronos + IANA/Windows timezone fallback). A bad
    // schedule yields null - the scheduler's Phase-1 tick re-derives NextRunAt on the next pass.
    private static DateTimeOffset? ComputeNextRunAt(string schedule, string? timeZone)
    {
        try
        {
            var tz = ResolveTimeZone(timeZone);
            var expr = Cronos.CronExpression.Parse(schedule, Cronos.CronFormat.Standard);
            return expr.GetNextOccurrence(DateTimeOffset.UtcNow, tz);
        }
        catch
        {
            return null;
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone)
            || timeZone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone, out var ianaId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZone, out var windowsId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.Utc;
    }
}
