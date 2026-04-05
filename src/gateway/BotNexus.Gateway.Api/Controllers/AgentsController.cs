using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
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

    /// <summary>Stops a specific agent instance.</summary>
    [HttpPost("{agentId}/sessions/{sessionId}/stop")]
    public async Task<ActionResult> StopInstance(string agentId, string sessionId, CancellationToken ct)
    {
        await _supervisor.StopAsync(agentId, sessionId, ct);
        return NoContent();
    }
}
