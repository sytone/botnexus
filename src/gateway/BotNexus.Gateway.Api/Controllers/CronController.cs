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

        var saved = await store.UpdateAsync(updated, cancellationToken);
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
}
