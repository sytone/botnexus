using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for agent registration and lifecycle management.
/// </summary>
/// <summary>
/// Represents agents controller.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class AgentsController : ControllerBase
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentSupervisor _supervisor;
    private readonly IAgentConfigurationWriter _configurationWriter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentsController"/> class.
    /// </summary>
    /// <param name="registry">The agent registry for accessing registered agents.</param>
    /// <param name="supervisor">The agent supervisor for managing agent instances and their lifecycle.</param>
    /// <param name="configurationWriter">Persists mutable agent configuration changes.</param>
    public AgentsController(IAgentRegistry registry, IAgentSupervisor supervisor, IAgentConfigurationWriter configurationWriter)
    {
        _registry = registry;
        _supervisor = supervisor;
        _configurationWriter = configurationWriter;
    }

    /// <summary>Lists all registered agents.</summary>
    /// <summary>
    /// Executes list.
    /// </summary>
    /// <returns>The list result.</returns>
    [HttpGet]
    public ActionResult<IReadOnlyList<AgentDescriptor>> List() => Ok(_registry.GetAll());

    /// <summary>Gets a specific agent by ID.</summary>
    /// <summary>
    /// Executes get.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The get result.</returns>
    [HttpGet("{agentId}")]
    public ActionResult<AgentDescriptor> Get(string agentId)
    {
        var descriptor = _registry.Get(AgentId.From(agentId));
        return descriptor is not null ? Ok(descriptor) : NotFound();
    }

    /// <summary>Registers a new agent.</summary>
    /// <summary>
    /// Executes register.
    /// </summary>
    /// <param name="descriptor">The descriptor.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The register result.</returns>
    [HttpPost]
    public async Task<ActionResult> Register([FromBody] AgentDescriptor descriptor, CancellationToken cancellationToken)
    {
        try
        {
            _registry.Register(descriptor);
            await _configurationWriter.SaveAsync(descriptor, cancellationToken);
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated descriptor when found; otherwise 404.</returns>
    /// <remarks>
    /// If <paramref name="descriptor" /> omits <see cref="AgentDescriptor.AgentId" />, the route value is used.
    /// If both are provided, they must match.
    /// </remarks>
    [HttpPut("{agentId}")]
    [ProducesResponseType(typeof(AgentDescriptor), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentDescriptor>> Update(string agentId, [FromBody] AgentDescriptor descriptor, CancellationToken cancellationToken)
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
            ? descriptor with { AgentId = AgentId.From(agentId) }
            : descriptor;

        var wasUpdated = _registry.Update(AgentId.From(agentId), updatedDescriptor);
        if (!wasUpdated)
            return NotFound();

        await _configurationWriter.SaveAsync(updatedDescriptor, cancellationToken);
        return Ok(updatedDescriptor);
    }

    /// <summary>Unregisters an agent.</summary>
    /// <summary>
    /// Executes unregister.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The unregister result.</returns>
    [HttpDelete("{agentId}")]
    public async Task<ActionResult> Unregister(string agentId, CancellationToken cancellationToken)
    {
        _registry.Unregister(AgentId.From(agentId));
        await _configurationWriter.DeleteAsync(agentId, cancellationToken);
        return NoContent();
    }

    /// <summary>Gets the status of a running agent instance.</summary>
    /// <summary>
    /// Executes get instance status.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The get instance status result.</returns>
    [HttpGet("{agentId}/sessions/{sessionId}/status")]
    public ActionResult<AgentInstance> GetInstanceStatus(string agentId, string sessionId)
    {
        var instance = _supervisor.GetInstance(AgentId.From(agentId), SessionId.From(sessionId));
        return instance is not null ? Ok(instance) : NotFound();
    }

    /// <summary>Lists all active agent instances.</summary>
    /// <summary>
    /// Executes list instances.
    /// </summary>
    /// <returns>The list instances result.</returns>
    [HttpGet("instances")]
    public ActionResult<IReadOnlyList<AgentInstance>> ListInstances() => Ok(_supervisor.GetAllInstances());

    /// <summary>Gets runtime health for active instances of an agent.</summary>
    /// <summary>
    /// Executes get health.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The get health result.</returns>
    [HttpGet("{agentId}/health")]
    public async Task<ActionResult<AgentHealthResponse>> GetHealth(string agentId, CancellationToken cancellationToken)
    {
        if (_registry.Get(AgentId.From(agentId)) is null)
            return NotFound();

        var instances = (_supervisor.GetAllInstances() ?? [])
            .Where(instance => string.Equals(instance.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (instances.Count == 0)
            return Ok(new AgentHealthResponse("unknown", AgentId.From(agentId), 0));

        if (_supervisor is not IAgentHandleInspector inspector)
            return Ok(new AgentHealthResponse("unknown", AgentId.From(agentId), instances.Count));

        var evaluatedCount = 0;
        foreach (var instance in instances)
        {
            var handle = inspector.GetHandle(instance.AgentId, instance.SessionId);
            if (handle is not IHealthCheckable healthCheckable)
                continue;

            evaluatedCount++;
            if (!await healthCheckable.PingAsync(cancellationToken))
                return Ok(new AgentHealthResponse("unhealthy", AgentId.From(agentId), instances.Count));
        }

        var status = evaluatedCount > 0 ? "healthy" : "unknown";
        return Ok(new AgentHealthResponse(status, AgentId.From(agentId), instances.Count));
    }

    /// <summary>Stops a specific agent instance.</summary>
    /// <summary>
    /// Executes stop instance.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stop instance result.</returns>
    [HttpPost("{agentId}/sessions/{sessionId}/stop")]
    public async Task<ActionResult> StopInstance(string agentId, string sessionId, CancellationToken cancellationToken)
    {
        await _supervisor.StopAsync(AgentId.From(agentId), SessionId.From(sessionId), cancellationToken);
        return NoContent();
    }
}
