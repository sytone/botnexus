using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Cron;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly IAgentConfigurationWriter _configurationWriter;
    private readonly IReadOnlyList<IAgentChangeNotifier> _agentChangeNotifiers;
    private readonly IHeartbeatProvisioner? _heartbeatProvisioner;
    private readonly ModelRegistry? _modelRegistry;
    private readonly ILogger<AgentsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentsController"/> class.
    /// </summary>
    public AgentsController(
        IAgentRegistry registry,
        IAgentSupervisor supervisor,
        IAgentConfigurationWriter configurationWriter,
        IEnumerable<IAgentChangeNotifier>? agentChangeNotifiers = null,
        IHeartbeatProvisioner? heartbeatProvisioner = null,
        ModelRegistry? modelRegistry = null,
        ILogger<AgentsController>? logger = null)
    {
        _registry = registry;
        _supervisor = supervisor;
        _configurationWriter = configurationWriter;
        _agentChangeNotifiers = agentChangeNotifiers?.ToArray() ?? [];
        _heartbeatProvisioner = heartbeatProvisioner;
        _modelRegistry = modelRegistry;
        _logger = logger ?? NullLogger<AgentsController>.Instance;
    }

    /// <summary>Lists all registered agents.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<AgentDescriptor>> List() => Ok(_registry.GetAll());

    /// <summary>Gets a specific agent by ID.</summary>
    [HttpGet("{agentId}")]
    public ActionResult<AgentDescriptor> Get(string agentId)
    {
        var typedAgentId = AgentId.From(agentId);
        var descriptor = _registry.Get(typedAgentId);
        return descriptor is not null ? Ok(descriptor) : NotFound();
    }

    /// <summary>Registers a new agent.</summary>
    [HttpPost]
    public async Task<ActionResult> Register([FromBody] AgentDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (descriptor.Kind == AgentKind.SubAgent)
        {
            return BadRequest(new
            {
                error = "Kind = SubAgent is reserved for runtime-spawned sub-agents and may not be registered via the REST API."
            });
        }

        var capabilityErrors = BotNexus.Gateway.Agents.AgentDescriptorValidator.ValidateModelCapabilities(descriptor, _modelRegistry);
        if (capabilityErrors.Count > 0)
            return BadRequest(new { error = string.Join(" ", capabilityErrors) });

        try
        {
            _registry.Register(descriptor);
            await _configurationWriter.SaveAsync(descriptor, cancellationToken);

            if (_heartbeatProvisioner is not null)
                await _heartbeatProvisioner.ProvisionAsync(descriptor, cancellationToken);

            await NotifyAgentsChangedBestEffortAsync("added", descriptor.AgentId.Value, cancellationToken);
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
    [HttpPut("{agentId}")]
    [ProducesResponseType(typeof(AgentDescriptor), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentDescriptor>> Update(string agentId, [FromBody] AgentDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!string.Equals(agentId, descriptor.AgentId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                error = $"Route agentId '{agentId}' does not match payload agentId '{descriptor.AgentId}'."
            });
        }

        if (descriptor.Kind == AgentKind.SubAgent)
        {
            return BadRequest(new
            {
                error = "Kind = SubAgent is reserved for runtime-spawned sub-agents and may not be set via the REST API."
            });
        }

        var capabilityErrors = BotNexus.Gateway.Agents.AgentDescriptorValidator.ValidateModelCapabilities(descriptor, _modelRegistry);
        if (capabilityErrors.Count > 0)
            return BadRequest(new { error = string.Join(" ", capabilityErrors) });

        var typedAgentId = AgentId.From(agentId);
        var updatedDescriptor = descriptor;

        var wasUpdated = _registry.Update(typedAgentId, updatedDescriptor);
        if (!wasUpdated)
            return NotFound();

        await _configurationWriter.SaveAsync(updatedDescriptor, cancellationToken);

        if (_heartbeatProvisioner is not null)
            await _heartbeatProvisioner.ProvisionAsync(updatedDescriptor, cancellationToken);

        await NotifyAgentsChangedBestEffortAsync("updated", updatedDescriptor.AgentId.Value, cancellationToken);
        return Ok(updatedDescriptor);
    }

    /// <summary>Unregisters an agent.</summary>
    [HttpDelete("{agentId}")]
    public async Task<ActionResult> Unregister(string agentId, CancellationToken cancellationToken)
    {
        var typedAgentId = AgentId.From(agentId);
        var existingDescriptor = _registry.Get(typedAgentId);

        _registry.Unregister(typedAgentId);
        await _configurationWriter.DeleteAsync(agentId, cancellationToken);

        if (existingDescriptor is not null)
            await NotifyAgentsChangedBestEffortAsync("removed", agentId, cancellationToken);

        return NoContent();
    }

    /// <summary>Gets the status of a running agent instance.</summary>
    [HttpGet("{agentId}/sessions/{sessionId}/status")]
    public ActionResult<AgentInstance> GetInstanceStatus(string agentId, string sessionId)
    {
        var typedAgentId = AgentId.From(agentId);
        var typedSessionId = SessionId.From(sessionId);
        var instance = _supervisor.GetInstance(typedAgentId, typedSessionId);
        return instance is not null ? Ok(instance) : NotFound();
    }

    /// <summary>Lists all active agent instances.</summary>
    [HttpGet("instances")]
    public ActionResult<IReadOnlyList<AgentInstance>> ListInstances() => Ok(_supervisor.GetAllInstances());

    /// <summary>Gets runtime health for active instances of an agent.</summary>
    [HttpGet("{agentId}/health")]
    public async Task<ActionResult<AgentHealthResponse>> GetHealth(string agentId, CancellationToken cancellationToken)
    {
        var typedAgentId = AgentId.From(agentId);

        if (_registry.Get(typedAgentId) is null)
            return NotFound();

        var instances = (_supervisor.GetAllInstances() ?? [])
            .Where(instance => string.Equals(instance.AgentId.Value, agentId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (instances.Count == 0)
            return Ok(new AgentHealthResponse("unknown", typedAgentId, 0));

        if (_supervisor is not IAgentHandleInspector inspector)
            return Ok(new AgentHealthResponse("unknown", typedAgentId, instances.Count));

        var evaluatedCount = 0;
        foreach (var instance in instances)
        {
            var handle = inspector.GetHandle(instance.AgentId, instance.SessionId);
            if (handle is not IHealthCheckable healthCheckable)
                continue;

            evaluatedCount++;
            if (!await healthCheckable.PingAsync(cancellationToken))
                return Ok(new AgentHealthResponse("unhealthy", typedAgentId, instances.Count));
        }

        var status = evaluatedCount > 0 ? "healthy" : "unknown";
        return Ok(new AgentHealthResponse(status, typedAgentId, instances.Count));
    }

    /// <summary>Stops a specific agent instance.</summary>
    [HttpPost("{agentId}/sessions/{sessionId}/stop")]
    public async Task<ActionResult> StopInstance(string agentId, string sessionId, CancellationToken cancellationToken)
    {
        var typedAgentId = AgentId.From(agentId);
        var typedSessionId = SessionId.From(sessionId);
        await _supervisor.StopAsync(typedAgentId, typedSessionId, cancellationToken);
        return NoContent();
    }

    /// <summary>Context summary for a running agent session.</summary>
    [HttpGet("{agentId}/sessions/{sessionId}/context")]
    public ActionResult GetContext(string agentId, string sessionId)
    {
        var handle = GetAgentHandle(agentId, sessionId);
        if (handle is null) return NotFound("No active handle.");

        var diag = (handle as IAgentHandleInspector)?.GetContextDiagnostics();
        if (diag is null) return NotFound("Handle does not support diagnostics.");

        const int contextWindowTokens = 128000;
        return Ok(new
        {
            agentId,
            sessionId,
            totalEstimatedTokens = diag.TotalEstimatedTokens,
            contextWindowTokens,
            usagePercent = Math.Round((double)diag.TotalEstimatedTokens / contextWindowTokens * 100, 1),
            sections = new
            {
                systemPrompt = new { tokens = diag.SystemPromptTokens, chars = diag.SystemPromptChars },
                toolDefinitions = new { tokens = diag.ToolDefinitionTokens, toolCount = diag.ToolCount },
                conversationHistory = new { tokens = diag.HistoryTokens, entryCount = diag.HistoryEntryCount }
            }
        });
    }

    /// <summary>Full system prompt for a running agent session.</summary>
    [HttpGet("{agentId}/sessions/{sessionId}/context/system-prompt")]
    public ActionResult GetSystemPrompt(string agentId, string sessionId)
    {
        var handle = GetAgentHandle(agentId, sessionId);
        if (handle is null) return NotFound("No active handle.");

        var diag = (handle as IAgentHandleInspector)?.GetContextDiagnostics();
        if (diag is null) return NotFound("Handle does not support diagnostics.");

        return Ok(new
        {
            systemPrompt = diag.SystemPrompt,
            chars = diag.SystemPromptChars,
            estimatedTokens = diag.SystemPromptTokens
        });
    }

    /// <summary>Tool definitions for a running agent session.</summary>
    [HttpGet("{agentId}/sessions/{sessionId}/context/tools")]
    public ActionResult GetTools(string agentId, string sessionId)
    {
        var handle = GetAgentHandle(agentId, sessionId);
        if (handle is null) return NotFound("No active handle.");

        var diag = (handle as IAgentHandleInspector)?.GetContextDiagnostics();
        if (diag is null) return NotFound("Handle does not support diagnostics.");

        return Ok(new { toolCount = diag.ToolCount, tools = diag.Tools });
    }

    /// <summary>Export full context to logs directory.</summary>
    [HttpPost("{agentId}/sessions/{sessionId}/context/export")]
    public ActionResult ExportContext(string agentId, string sessionId)
    {
        var handle = GetAgentHandle(agentId, sessionId);
        if (handle is null) return NotFound("No active handle.");

        var diag = (handle as IAgentHandleInspector)?.GetContextDiagnostics();
        if (diag is null) return NotFound("Handle does not support diagnostics.");

        var logDir = Path.Combine(BotNexusHome.ResolveHomePath(), "logs");
        Directory.CreateDirectory(logDir);

        var fileName = $"context-export-{agentId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json";
        var filePath = Path.Combine(logDir, fileName);

        System.IO.File.WriteAllText(
            filePath,
            System.Text.Json.JsonSerializer.Serialize(diag, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return Ok(new { exported = filePath });
    }

    private IAgentHandle? GetAgentHandle(string agentId, string sessionId)
    {
        var typedAgentId = AgentId.From(agentId);
        var typedSessionId = SessionId.From(sessionId);
        var supervisorImpl = _supervisor as BotNexus.Gateway.Agents.DefaultAgentSupervisor;
        return supervisorImpl?.GetHandle(typedAgentId, typedSessionId);
    }

    private async Task NotifyAgentsChangedBestEffortAsync(string changeType, string? agentId, CancellationToken cancellationToken)
    {
        if (_agentChangeNotifiers.Count == 0)
            return;

        foreach (var notifier in _agentChangeNotifiers)
        {
            try
            {
                await notifier.NotifyAgentsChangedAsync(changeType, agentId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to publish agents change notification ({ChangeType}) for agent {AgentId} via notifier {NotifierType}.",
                    changeType,
                    agentId,
                    notifier.GetType().FullName);
            }
        }
    }
}
