using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Registration values accepted when adding a repository.</summary>
/// <param name="Id">Stable lowercase registration key.</param>
/// <param name="RepositoryUrl">Absolute repository URL.</param>
/// <param name="RequestedRef">Branch, tag, or commit requested for future reconciliation.</param>
/// <param name="Enabled">Whether the registration is enabled.</param>
/// <param name="UpdatesEnabled">Whether future automatic updates are allowed.</param>
public sealed record ExtensionRepositoryCreateRequest(string Id, string RepositoryUrl, string RequestedRef, bool Enabled, bool UpdatesEnabled);
/// <summary>Mutable values accepted when editing a repository registration.</summary>
/// <param name="RepositoryUrl">Replacement URL, or null to preserve it.</param>
/// <param name="RequestedRef">Replacement ref, or null to preserve it.</param>
/// <param name="UpdatesEnabled">Replacement update preference, or null to preserve it.</param>
public sealed record ExtensionRepositoryUpdateRequest(string? RepositoryUrl, string? RequestedRef, bool? UpdatesEnabled);
/// <summary>Explicit enabled state for a repository registration.</summary>
/// <param name="Enabled">Whether the registration should be enabled.</param>
public sealed record ExtensionRepositoryEnabledRequest(bool Enabled);
/// <summary>Repository registration and its most recently persisted reconciliation state.</summary>
/// <param name="Id">Registration key.</param>
/// <param name="RepositoryUrl">Configured URL.</param>
/// <param name="RequestedRef">Configured branch, tag, or commit.</param>
/// <param name="Enabled">Whether the registration is enabled.</param>
/// <param name="UpdatesEnabled">Whether future updates are allowed.</param>
/// <param name="ReconciliationStatus">Truthful reconciliation state.</param>
/// <param name="ResolvedCommit">Resolved commit, unavailable before reconciliation.</param>
/// <param name="ClonePath">Clone path, unavailable before reconciliation.</param>
/// <param name="LastAttemptUtc">Last attempt, unavailable before reconciliation.</param>
/// <param name="LastSuccessUtc">Last success, unavailable before reconciliation.</param>
/// <param name="DeployedVersion">Deployed version, unavailable before reconciliation.</param>
/// <param name="LatestFailure">Latest named failure, unavailable before reconciliation.</param>
/// <param name="SyncAvailable">Whether an operational sync action exists.</param>
public sealed record ExtensionRepositoryResponse(string Id, string RepositoryUrl, string RequestedRef, bool Enabled, bool UpdatesEnabled, string ReconciliationStatus, string? ResolvedCommit, string? ClonePath, DateTimeOffset? LastAttemptUtc, DateTimeOffset? LastSuccessUtc, string? DeployedVersion, string? LatestFailure, bool SyncAvailable);

/// <summary>Manages extension repository registrations without materializing or executing their contents.</summary>
[ApiController]
[Route("api/extension-repositories")]
public sealed class ExtensionRepositoriesController(ExtensionRepositoryRegistryService registry) : ControllerBase
{
    private readonly ExtensionRepositoryRegistryService _registry = registry;

    /// <summary>Lists registrations without claiming unavailable runtime state.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ExtensionRepositoryResponse>>> List(CancellationToken ct = default)
        => Ok((await _registry.ListAsync(ct)).Select(Project).ToArray());

    /// <summary>Adds configuration metadata only; repository content is not fetched or executed.</summary>
    [HttpPost]
    public async Task<ActionResult<ExtensionRepositoryResponse>> Add([FromBody] ExtensionRepositoryCreateRequest request, CancellationToken ct = default)
    {
        try
        {
            await _registry.AddAsync(request.Id, request.RepositoryUrl, request.RequestedRef, request.Enabled, request.UpdatesEnabled, ct);
            var item = (await _registry.ListAsync(ct)).Single(x => x.Id == request.Id);
            return Created($"/api/extension-repositories/{Uri.EscapeDataString(request.Id)}", Project(item));
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    /// <summary>Edits mutable registration metadata.</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<ExtensionRepositoryResponse>> Update(string id, [FromBody] ExtensionRepositoryUpdateRequest request, CancellationToken ct = default)
    {
        try
        {
            await _registry.UpdateAsync(id, request.RepositoryUrl, request.RequestedRef, request.UpdatesEnabled, ct);
            return Ok(Project((await _registry.ListAsync(ct)).Single(x => x.Id == id)));
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>Enables or disables a registration.</summary>
    [HttpPut("{id}/enabled")]
    public async Task<IActionResult> SetEnabled(string id, [FromBody] ExtensionRepositoryEnabledRequest request, CancellationToken ct = default)
    {
        try { await _registry.SetEnabledAsync(id, request.Enabled, ct); return NoContent(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>Removes registration metadata without touching repository contents.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id, CancellationToken ct = default)
    {
        try { await _registry.RemoveAsync(id, ct); return NoContent(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>Reports that synchronization is unavailable until the reconciler is implemented.</summary>
    [HttpPost("{id}/sync")]
    public async Task<IActionResult> SyncNow(string id, CancellationToken ct = default)
    {
        if (!(await _registry.ListAsync(ct)).Any(x => x.Id == id)) return NotFound(new { error = $"Extension repository '{id}' was not found." });
        return StatusCode(StatusCodes.Status501NotImplemented, new { error = "Sync is unavailable until extension repository reconciliation is implemented." });
    }

    private static ExtensionRepositoryResponse Project(ExtensionRepositoryRegistrationInfo item) => new(
        item.Id, item.RepositoryUrl, item.RequestedRef, item.Enabled, item.UpdatesEnabled,
        item.ReconciliationStatus ?? "not-yet-reconciled", item.ResolvedCommit, item.ClonePath,
        item.LastAttemptUtc, item.LastSuccessUtc, null, item.LatestFailure, false);
}
