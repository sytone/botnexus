using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Memory;
using BotNexus.Memory.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST endpoints for inspecting per-agent memory store statistics.
/// </summary>
[ApiController]
[Route("api/memory")]
public sealed class MemoryController(
    IAgentRegistry agentRegistry,
    IMemoryStoreFactory memoryStoreFactory,
    ILogger<MemoryController> logger) : ControllerBase
{
    /// <summary>
    /// Lists all agents that have memory enabled along with their store statistics.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListMemoryStores(CancellationToken ct)
    {
        var agents = agentRegistry.GetAll()
            .Where(a => a.Memory is { Enabled: true })
            .OrderBy(a => a.AgentId.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<MemoryStoreDto>(agents.Count);
        foreach (var agent in agents)
        {
            var dto = await GetStatsForAgentAsync(agent.AgentId.Value, ct).ConfigureAwait(false);
            if (dto is not null)
                results.Add(dto);
        }

        return Ok(results);
    }

    /// <summary>
    /// Gets memory store statistics for a specific agent.
    /// </summary>
    [HttpGet("{agentId}")]
    public async Task<IActionResult> GetMemoryStore(string agentId, CancellationToken ct)
    {
        var descriptor = agentRegistry.Get(AgentId.From(agentId));
        if (descriptor is null)
            return NotFound(new { error = $"Agent '{agentId}' not found." });

        if (descriptor.Memory is not { Enabled: true })
            return NotFound(new { error = $"Agent '{agentId}' does not have memory enabled." });

        var dto = await GetStatsForAgentAsync(agentId, ct).ConfigureAwait(false);
        return dto is not null ? Ok(dto) : NotFound(new { error = $"Memory store for agent '{agentId}' is not available." });
    }

    /// <summary>
    /// Searches memory entries for a specific agent. Requires a query parameter.
    /// </summary>
    [HttpGet("{agentId}/entries")]
    public async Task<IActionResult> SearchEntries(
        string agentId,
        [FromQuery] string? query = null,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var descriptor = agentRegistry.Get(AgentId.From(agentId));
        if (descriptor is null)
            return NotFound(new { error = $"Agent '{agentId}' not found." });

        if (descriptor.Memory is not { Enabled: true })
            return NotFound(new { error = $"Agent '{agentId}' does not have memory enabled." });

        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { error = "Query parameter is required for entry search." });

        limit = Math.Clamp(limit, 1, 100);

        try
        {
            var store = memoryStoreFactory.Create(agentId);
            await store.InitializeAsync(ct).ConfigureAwait(false);

            var entries = await store.SearchAsync(query, limit, ct: ct).ConfigureAwait(false);

            var dtos = entries.Select(e => new MemoryEntryDto(
                Id: e.Id,
                CreatedAt: e.CreatedAt,
                SourceType: e.SourceType,
                SessionId: e.SessionId,
                ContentPreview: e.Content.Length > 200 ? e.Content[..200] + "..." : e.Content
            )).ToList();

            return Ok(new { agentId, query, entries = dtos, count = dtos.Count });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to search memory entries for agent '{AgentId}'.", agentId);
            return StatusCode(500, new { error = "Failed to access memory store." });
        }
    }

    private async Task<MemoryStoreDto?> GetStatsForAgentAsync(string agentId, CancellationToken ct)
    {
        try
        {
            var store = memoryStoreFactory.Create(agentId);
            await store.InitializeAsync(ct).ConfigureAwait(false);
            var stats = await store.GetStatsAsync(ct).ConfigureAwait(false);
            return new MemoryStoreDto(
                AgentId: agentId,
                EntryCount: stats.EntryCount,
                DatabaseSizeBytes: stats.DatabaseSizeBytes,
                LastIndexedAt: stats.LastIndexedAt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get memory stats for agent '{AgentId}'.", agentId);
            return null;
        }
    }
}

internal sealed record MemoryStoreDto(
    string AgentId,
    int EntryCount,
    long DatabaseSizeBytes,
    DateTimeOffset? LastIndexedAt);

internal sealed record MemoryEntryDto(
    string Id,
    DateTimeOffset CreatedAt,
    string SourceType,
    string? SessionId,
    string ContentPreview);
