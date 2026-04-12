using BotNexus.Cron;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for cron job management and execution.
/// </summary>
/// <summary>
/// Represents cron controller.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class CronController(ICronStore store, CronScheduler scheduler) : ControllerBase
{
    /// <summary>Lists cron jobs.</summary>
    /// <summary>
    /// Executes list.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list result.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CronJob>>> List(CancellationToken cancellationToken)
        => Ok(await store.ListAsync(ct: cancellationToken));

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
        var job = await store.GetAsync(jobId, cancellationToken);
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
            Id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id,
            CreatedAt = request.CreatedAt == default ? DateTimeOffset.UtcNow : request.CreatedAt
        };

        var created = await store.CreateAsync(toCreate, cancellationToken);
        return CreatedAtAction(nameof(Get), new { jobId = created.Id }, created);
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
        var existing = await store.GetAsync(jobId, cancellationToken);
        if (existing is null)
            return NotFound();

        var updated = request with
        {
            Id = jobId,
            CreatedAt = existing.CreatedAt
        };

        return Ok(await store.UpdateAsync(updated, cancellationToken));
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
        await store.DeleteAsync(jobId, cancellationToken);
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
        var existing = await store.GetAsync(jobId, cancellationToken);
        if (existing is null)
            return NotFound();

        var run = await scheduler.RunNowAsync(jobId, cancellationToken);
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
        var existing = await store.GetAsync(jobId, cancellationToken);
        if (existing is null)
            return NotFound();

        return Ok(await store.GetRunHistoryAsync(jobId, limit, cancellationToken));
    }
}
