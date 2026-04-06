using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for agent registration and lifecycle management.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class AgentsController : ControllerBase
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentSupervisor _supervisor;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentsController"/> class.
    /// </summary>
    /// <param name="registry">The agent registry for accessing registered agents.</param>
    /// <param name="supervisor">The agent supervisor for managing agent instances and their lifecycle.</param>
    public AgentsController(IAgentRegistry registry, IAgentSupervisor supervisor)
    {
        _registry = registry;
        _supervisor = supervisor;
    }

    /// <summary>Lists all registered agents.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<AgentDescriptor>> List() => Ok(_registry.GetAll());

    /// <summary>Gets a specific agent by ID.</summary>
    [HttpGet("{agentId}")]
    public ActionResult<AgentDescriptor> Get(string agentId)
    {
        var descriptor = _registry.Get(agentId);
        return descriptor is not null ? Ok(descriptor) : NotFound();
    }

    /// <summary>Registers a new agent.</summary>
    [HttpPost]
    public ActionResult Register([FromBody] AgentDescriptor descriptor)
    {
        try
        {
            _registry.Register(descriptor);
            return CreatedAtAction(nameof(Get), new { agentId = descriptor.AgentId }, descriptor);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing agent descriptor.
    /// </summary>
    /// <param name="agentId">The route agent identifier.</param>
    /// <param name="descriptor">The descriptor payload to persist.</param>
    /// <returns>The updated descriptor when found; otherwise 404.</returns>
    /// <remarks>
    /// If <paramref name="descriptor" /> omits <see cref="AgentDescriptor.AgentId" />, the route value is used.
    /// If both are provided, they must match.
    /// </remarks>
    [HttpPut("{agentId}")]
    [ProducesResponseType(typeof(AgentDescriptor), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AgentDescriptor> Update(string agentId, [FromBody] AgentDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.AgentId) &&
            !string.Equals(agentId, descriptor.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                error = $"Route agentId '{agentId}' does not match payload agentId '{descriptor.AgentId}'."
            });
        }

        var updatedDescriptor = string.IsNullOrWhiteSpace(descriptor.AgentId)
            ? descriptor with { AgentId = agentId }
            : descriptor;

        var wasUpdated = _registry.Update(agentId, updatedDescriptor);
        return wasUpdated ? Ok(updatedDescriptor) : NotFound();
    }

    /// <summary>Unregisters an agent.</summary>
    [HttpDelete("{agentId}")]
    public ActionResult Unregister(string agentId)
    {
        _registry.Unregister(agentId);
        return NoContent();
    }

    /// <summary>Gets the status of a running agent instance.</summary>
    [HttpGet("{agentId}/sessions/{sessionId}/status")]
    public ActionResult<AgentInstance> GetInstanceStatus(string agentId, string sessionId)
    {
        var instance = _supervisor.GetInstance(agentId, sessionId);
        return instance is not null ? Ok(instance) : NotFound();
    }

    /// <summary>Lists all active agent instances.</summary>
    [HttpGet("instances")]
    public ActionResult<IReadOnlyList<AgentInstance>> ListInstances() => Ok(_supervisor.GetAllInstances());

    /// <summary>Gets runtime health for active instances of an agent.</summary>
    [HttpGet("{agentId}/health")]
    public async Task<ActionResult<AgentHealthResponse>> GetHealth(string agentId, CancellationToken cancellationToken)
    {
        if (_registry.Get(agentId) is null)
            return NotFound();

        var instances = (_supervisor.GetAllInstances() ?? [])
            .Where(instance => string.Equals(instance.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (instances.Count == 0)
            return Ok(new AgentHealthResponse("unknown", agentId, 0));

        if (_supervisor is not IAgentHandleInspector inspector)
            return Ok(new AgentHealthResponse("unknown", agentId, instances.Count));

        var evaluatedCount = 0;
        foreach (var instance in instances)
        {
            var handle = inspector.GetHandle(instance.AgentId, instance.SessionId);
            if (handle is not IHealthCheckable healthCheckable)
                continue;

            evaluatedCount++;
            if (!await healthCheckable.PingAsync(cancellationToken))
                return Ok(new AgentHealthResponse("unhealthy", agentId, instances.Count));
        }

        var status = evaluatedCount > 0 ? "healthy" : "unknown";
        return Ok(new AgentHealthResponse(status, agentId, instances.Count));
    }

    /// <summary>Stops a specific agent instance.</summary>
    [HttpPost("{agentId}/sessions/{sessionId}/stop")]
    public async Task<ActionResult> StopInstance(string agentId, string sessionId, CancellationToken cancellationToken)
    {
        await _supervisor.StopAsync(agentId, sessionId, cancellationToken);
        return NoContent();
    }
}
