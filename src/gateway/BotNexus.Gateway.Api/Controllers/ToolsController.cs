using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Tools;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for managing user-defined portal tools. Tools are persisted server-side so
/// they roam with the user across browsers and devices (#2232).
/// </summary>
[ApiController]
[Route("api/tools")]
public sealed class ToolsController(IToolStore store, ILogger<ToolsController> logger) : ControllerBase
{
    private readonly IToolStore _store = store;
    private readonly ILogger<ToolsController> _logger = logger;

    /// <summary>Lists all tools ordered by <see cref="ToolDefinition.Order"/> ascending.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The full ordered list of tools.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ToolDefinition>>> List(CancellationToken cancellationToken)
        => Ok(await _store.ListAsync(cancellationToken));

    /// <summary>Gets a single tool by identifier.</summary>
    /// <param name="id">The tool identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tool, or 404 when it does not exist.</returns>
    [HttpGet("{id}")]
    public async Task<ActionResult<ToolDefinition>> Get(string id, CancellationToken cancellationToken)
    {
        var tool = await _store.GetAsync(ToolId.From(id), cancellationToken);
        return tool is null ? NotFound(new { error = $"Tool '{id}' not found." }) : Ok(tool);
    }

    /// <summary>Creates a new tool.</summary>
    /// <param name="request">The tool to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created tool.</returns>
    [HttpPost]
    public async Task<ActionResult<ToolDefinition>> Create([FromBody] ToolDefinition request, CancellationToken cancellationToken)
    {
        var created = await _store.CreateAsync(request, cancellationToken);
        _logger.LogInformation("Tool created via API: {ToolId} ({Name})", created.Id.Value, created.Name);
        return CreatedAtAction(nameof(Get), new { id = created.Id.Value }, created);
    }

    /// <summary>Updates an existing tool.</summary>
    /// <param name="id">The tool identifier.</param>
    /// <param name="request">The updated tool payload.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated tool, or 404 when it does not exist.</returns>
    [HttpPut("{id}")]
    [HttpPatch("{id}")]
    public async Task<ActionResult<ToolDefinition>> Update(string id, [FromBody] ToolDefinition request, CancellationToken cancellationToken)
    {
        var typedId = ToolId.From(id);
        var existing = await _store.GetAsync(typedId, cancellationToken);
        if (existing is null)
            return NotFound(new { error = $"Tool '{id}' not found." });

        var updated = request with
        {
            Id = typedId,
            CreatedAt = existing.CreatedAt
        };

        var saved = await _store.UpdateAsync(updated, cancellationToken);
        _logger.LogInformation("Tool updated via API: {ToolId} ({Name})", saved.Id.Value, saved.Name);
        return Ok(saved);
    }

    /// <summary>Deletes a tool.</summary>
    /// <param name="id">The tool identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>204 No Content, or 404 when the tool does not exist.</returns>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var typedId = ToolId.From(id);
        var existing = await _store.GetAsync(typedId, cancellationToken);
        if (existing is null)
            return NotFound(new { error = $"Tool '{id}' not found." });

        await _store.DeleteAsync(typedId, cancellationToken);
        _logger.LogInformation("Tool deleted via API: {ToolId}", id);
        return NoContent();
    }
}
