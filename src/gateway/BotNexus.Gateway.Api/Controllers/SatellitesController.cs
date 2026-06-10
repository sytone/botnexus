using BotNexus.Gateway.Abstractions.Satellites;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST endpoints for querying satellite node status and connection information.
/// </summary>
[ApiController]
[Route("api/satellites")]
public sealed class SatellitesController(ISatelliteRegistry registry) : ControllerBase
{
    private readonly ISatelliteRegistry _registry = registry;

    /// <summary>Lists all registered satellites with their current connection status.</summary>
    [HttpGet]
    public IActionResult GetAll()
    {
        var satellites = _registry.GetAll();
        return Ok(satellites.Select(s => new SatelliteStatusDto
        {
            Id = s.Id,
            DisplayName = s.DisplayName,
            Platform = s.Platform,
            OwnerUserId = s.OwnerUserId,
            Capabilities = s.Capabilities,
            Status = s.Status.ToString().ToLowerInvariant(),
            LastSeen = s.LastSeen,
            ConnectionId = s.ConnectionId
        }));
    }

    /// <summary>Gets a single satellite's status and connection information.</summary>
    [HttpGet("{satelliteId}")]
    public IActionResult GetById(string satelliteId)
    {
        var satellite = _registry.GetById(satelliteId);
        if (satellite is null)
            return NotFound(new { error = $"Satellite '{satelliteId}' not found." });

        return Ok(new SatelliteStatusDto
        {
            Id = satellite.Id,
            DisplayName = satellite.DisplayName,
            Platform = satellite.Platform,
            OwnerUserId = satellite.OwnerUserId,
            Capabilities = satellite.Capabilities,
            Status = satellite.Status.ToString().ToLowerInvariant(),
            LastSeen = satellite.LastSeen,
            ConnectionId = satellite.ConnectionId
        });
    }
}

/// <summary>Response DTO for satellite status queries.</summary>
public sealed class SatelliteStatusDto
{
    /// <summary>Satellite identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Platform (windows, macos, linux).</summary>
    public required string Platform { get; init; }

    /// <summary>Owner user ID.</summary>
    public required string OwnerUserId { get; init; }

    /// <summary>Capabilities.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Current status (online, offline, stale).</summary>
    public required string Status { get; init; }

    /// <summary>Last heartbeat time.</summary>
    public DateTimeOffset? LastSeen { get; init; }

    /// <summary>SignalR connection ID when online.</summary>
    public string? ConnectionId { get; init; }
}
