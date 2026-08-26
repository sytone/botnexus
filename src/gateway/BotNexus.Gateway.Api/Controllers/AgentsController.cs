using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Cron;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Webhooks;
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
    private readonly ISkillReviewProvisioner? _skillReviewProvisioner;
    private readonly IAgentWebhookProvisioner? _webhookProvisioner;
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
        ISkillReviewProvisioner? skillReviewProvisioner = null,
        ModelRegistry? modelRegistry = null,
        ILogger<AgentsController>? logger = null,
        IAgentWebhookProvisioner? webhookProvisioner = null)
    {
        _registry = registry;
        _supervisor = supervisor;
        _configurationWriter = configurationWriter;
        _agentChangeNotifiers = agentChangeNotifiers?.ToArray() ?? [];
        _heartbeatProvisioner = heartbeatProvisioner;
        _skillReviewProvisioner = skillReviewProvisioner;
        _webhookProvisioner = webhookProvisioner;
        _modelRegistry = modelRegistry;
        _logger = logger ?? NullLogger<AgentsController>.Instance;
    }

    /// <summary>
    /// Lists registered agents. By default returns only first-class, user-facing agents:
    /// runtime-spawned sub-agents (<see cref="AgentKind.SubAgent"/>) and built-in platform
    /// archetype agents (<c>metadata.builtin == true</c>) are excluded so the portal agent
    /// picker is not cluttered with infrastructure descriptors.
    /// </summary>
    /// <param name="includeSubAgents">
    /// When <c>true</c>, include runtime-spawned sub-agent descriptors (e.g. "Farnsworth (coder)").
    /// These are ephemeral children created via <c>spawn_subagent</c> and are hidden by default.
    /// Useful for diagnostics / observability of what sub-agents exist.
    /// </param>
    /// <param name="includeBuiltin">
    /// When <c>true</c>, include built-in platform archetype agents (researcher, coder, planner,
    /// reviewer, writer, analyst). These are spawn/converse targets rather than top-level
    /// user-created agents and are hidden by default.
    /// </param>
    /// <remarks>
    /// #2755: this endpoint returns the lean <see cref="AgentListItem"/> projection, not the full
    /// <see cref="AgentDescriptor"/>. The domain model was previously serialised directly, putting 36
    /// properties per agent on the portal's cold-boot path where consumers read at most seven, and
    /// broadcasting <c>systemPrompt</c>/<c>fileAccess</c>/<c>toolPolicy</c>/<c>extensionConfig</c> on a
    /// call that is unauthenticated by default (#506). Clients needing the full shape use
    /// <c>GET /api/agents/{agentId}</c>.
    /// </remarks>
    [HttpGet]
    public ActionResult<IReadOnlyList<AgentListItem>> List(
        [FromQuery] bool includeSubAgents = false,
        [FromQuery] bool includeBuiltin = false)
    {
        var agents = _registry.GetAll()
            .Where(a => includeSubAgents || a.Kind != AgentKind.SubAgent)
            .Where(a => includeBuiltin || !a.IsBuiltIn)
            .Select(AgentListItem.FromDescriptor)
            .ToList();
        return Ok(agents);
    }

    /// <summary>Gets a specific agent by ID.</summary>
    [HttpGet("{agentId}")]
    public ActionResult<AgentDescriptor> Get(string agentId)
    {
        if (!TryParseAgentId(agentId, out var typedAgentId, out var error))
            return BadRequest(new { error });
        var descriptor = _registry.Get(typedAgentId!.Value);
        return descriptor is not null ? Ok(descriptor) : NotFound();
    }

    /// <summary>
    /// Parses a route-supplied agent id, turning a blank-after-normalization or over-long value
    /// into a 400 rather than the 500 that <see cref="AgentId.From(string)"/> would produce.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentId"/> trims its input and rejects whitespace, so a route segment of
    /// <c>"%20"</c> is non-empty on the wire but blank after normalization. Left unguarded that
    /// throws out of the action and surfaces as an unhandled 500 - a validation failure reported
    /// as a server fault. Length is bounded here for the same reason
    /// <c>AgentDescriptorValidator</c> bounds it on the body: the id becomes a URL path segment
    /// and a downstream key.
    /// </remarks>
    private static bool TryParseAgentId(string? agentId, out AgentId? typedAgentId, out string? error)
    {
        typedAgentId = null;

        if (string.IsNullOrWhiteSpace(agentId))
        {
            error = "Agent id is required and must not be blank after trimming.";
            return false;
        }

        if (agentId.Trim().Length > BotNexus.Gateway.Agents.AgentDescriptorValidator.MaxAgentIdLength)
        {
            error = $"Agent id must be {BotNexus.Gateway.Agents.AgentDescriptorValidator.MaxAgentIdLength} characters or fewer.";
            return false;
        }

        typedAgentId = AgentId.From(agentId);
        error = null;
        return true;
    }

    /// <summary>
    /// Registers a new agent.
    /// </summary>
    /// <remarks>
    /// #2065: the create path is failure-atomic. The candidate descriptor is fully validated,
    /// then persisted to <c>config.json</c> <b>before</b> the in-memory registry is mutated, so a
    /// disk failure leaves no runtime/config divergence. If heartbeat or skill provisioning fails
    /// after the registry commit, the registry entry and the freshly-written config are rolled
    /// back (compensation) so the partial lifecycle effect is undone.
    /// </remarks>
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

        // #2065: reject incomplete descriptors up front. A descriptor missing DisplayName/ModelId/
        // ApiProvider/IsolationStrategy would, once persisted, clear those properties on the next
        // config reload. Validation happens before any registry or disk mutation.
        var validationErrors = BotNexus.Gateway.Agents.AgentDescriptorValidator.ValidateForConfig(descriptor, null, _modelRegistry);
        if (validationErrors.Count > 0)
            return BadRequest(new { error = string.Join(" ", validationErrors) });

        var agentId = descriptor.AgentId;
        if (_registry.Contains(agentId))
            return Conflict(new { error = $"Agent '{agentId.Value}' is already registered." });

        // 1) Persist config first. If the disk write fails, nothing has touched the registry.
        try
        {
            await _configurationWriter.SaveAsync(descriptor, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist config for new agent {AgentId}; registry not modified.", agentId.Value);
            return Problem(
                detail: $"Failed to persist agent configuration: {ex.Message}",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // 2) Commit the registry. A duplicate here is a race - surface as a conflict.
        try
        {
            _registry.Register(descriptor);
        }
        catch (InvalidOperationException ex)
        {
            await CompensateConfigDeleteAsync(agentId.Value, cancellationToken);
            return Conflict(new { error = ex.Message });
        }

        // 3) Provision downstream side effects. On failure, roll back both registry and config.
        try
        {
            if (_heartbeatProvisioner is not null)
                await _heartbeatProvisioner.ProvisionAsync(descriptor, cancellationToken);
            if (_skillReviewProvisioner is not null)
                await _skillReviewProvisioner.ProvisionAsync(descriptor, cancellationToken);
            // #3523: the webhook binding is part of what makes a newly created agent usable, so it
            // shares the create path's rollback semantics rather than being best-effort.
            if (_webhookProvisioner is not null)
                await _webhookProvisioner.ProvisionAsync(descriptor, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provisioning failed for new agent {AgentId}; rolling back registry and config.", agentId.Value);
            _registry.Unregister(agentId);
            await CompensateConfigDeleteAsync(agentId.Value, cancellationToken);
            return Problem(
                detail: $"Failed to provision agent side effects: {ex.Message}",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        await NotifyAgentsChangedBestEffortAsync("added", agentId.Value, cancellationToken);
        return CreatedAtAction(nameof(Get), new { agentId = descriptor.AgentId }, descriptor);
    }

    /// <summary>
    /// Updates an existing agent descriptor.
    /// </summary>
    /// <remarks>
    /// #2065: the update path is failure-atomic. The candidate descriptor is validated, then
    /// persisted before the registry is mutated. If persistence fails the registry keeps the
    /// previous descriptor; if provisioning fails after the registry commit both the registry and
    /// the config are restored to the previous descriptor.
    /// </remarks>
    [HttpPut("{agentId}")]
    [ProducesResponseType(typeof(AgentDescriptor), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentDescriptor>> Update(string agentId, [FromBody] AgentDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!TryParseAgentId(agentId, out _, out var routeError))
            return BadRequest(new { error = routeError });

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

        // #2065: reject incomplete descriptors before any mutation so an update cannot silently
        // clear persisted required properties.
        var validationErrors = BotNexus.Gateway.Agents.AgentDescriptorValidator.ValidateForConfig(descriptor, null, _modelRegistry);
        if (validationErrors.Count > 0)
            return BadRequest(new { error = string.Join(" ", validationErrors) });

        var typedAgentId = AgentId.From(agentId);
        var previous = _registry.Get(typedAgentId);
        if (previous is null)
            return NotFound();

        // 1) Persist config first. On failure the registry still holds the previous descriptor.
        try
        {
            await _configurationWriter.SaveAsync(descriptor, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist config for updated agent {AgentId}; registry unchanged.", agentId);
            return Problem(
                detail: $"Failed to persist agent configuration: {ex.Message}",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // 2) Commit the registry.
        var wasUpdated = _registry.Update(typedAgentId, descriptor);
        if (!wasUpdated)
        {
            // Concurrently removed between the Get and the Update; restore config to match.
            await CompensateConfigDeleteAsync(agentId, cancellationToken);
            return NotFound();
        }

        // 3) Provision downstream side effects. On failure, restore registry and config to previous.
        try
        {
            if (_heartbeatProvisioner is not null)
                await _heartbeatProvisioner.ProvisionAsync(descriptor, cancellationToken);
            if (_skillReviewProvisioner is not null)
                await _skillReviewProvisioner.ProvisionAsync(descriptor, cancellationToken);
            // #3523: a display-name change must reach the downstream target, otherwise it shows a
            // stale name indefinitely. ProvisionAsync is create-or-leave-alone, so this re-sends
            // the existing binding rather than re-keying it.
            if (_webhookProvisioner is not null)
                await _webhookProvisioner.ProvisionAsync(descriptor, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provisioning failed for updated agent {AgentId}; rolling back to previous descriptor.", agentId);
            _registry.Update(typedAgentId, previous);
            await CompensateConfigSaveAsync(previous, cancellationToken);
            return Problem(
                detail: $"Failed to provision agent side effects: {ex.Message}",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        await NotifyAgentsChangedBestEffortAsync("updated", descriptor.AgentId.Value, cancellationToken);
        return Ok(descriptor);
    }

    /// <summary>
    /// Unregisters an agent.
    /// </summary>
    /// <remarks>
    /// #2065: the delete path removes the persisted config <b>before</b> dropping the registry
    /// entry, so a disk failure leaves the agent both registered and persisted (consistent) rather
    /// than live-in-registry-only.
    /// </remarks>
    [HttpDelete("{agentId}")]
    public async Task<ActionResult> Unregister(string agentId, CancellationToken cancellationToken)
    {
        if (!TryParseAgentId(agentId, out var parsedAgentId, out var routeError))
            return BadRequest(new { error = routeError });

        var typedAgentId = parsedAgentId!.Value;
        var existingDescriptor = _registry.Get(typedAgentId);

        // 1) Delete config first. If this fails the registry still holds the agent (no divergence).
        try
        {
            await _configurationWriter.DeleteAsync(agentId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete config for agent {AgentId}; registry unchanged.", agentId);
            return Problem(
                detail: $"Failed to delete agent configuration: {ex.Message}",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // 2) Drop the registry entry.
        _registry.Unregister(typedAgentId);

        // 3) #3524: reclaim the system cron jobs the platform provisioned for this agent. Best-effort
        // and AFTER the registry commit: the agent is already gone, so a cron-store failure must not
        // turn a successful delete into a 500 and leave the caller believing the agent survived. The
        // worst case of a swallowed failure is the orphaned job we had before this fix, logged loudly.
        await DeprovisionBestEffortAsync(typedAgentId, cancellationToken);

        if (existingDescriptor is not null)
            await NotifyAgentsChangedBestEffortAsync("removed", agentId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Removes platform-provisioned per-agent cron jobs, never failing the delete.
    /// </summary>
    private async Task DeprovisionBestEffortAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        if (_heartbeatProvisioner is not null)
        {
            try
            {
                await _heartbeatProvisioner.DeprovisionAsync(agentId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to deprovision heartbeat cron job for deleted agent {AgentId}; the job may be orphaned.",
                    agentId);
            }
        }

        if (_skillReviewProvisioner is not null)
        {
            try
            {
                await _skillReviewProvisioner.DeprovisionAsync(agentId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to deprovision skill-review cron job for deleted agent {AgentId}; the job may be orphaned.",
                    agentId);
            }
        }

        // #3523: deliberately weaker than the create path. A downstream outage must never block
        // deleting an agent, so the registration removal and the target notification are
        // best-effort; the next startup reconciliation is the recovery path.
        if (_webhookProvisioner is not null)
        {
            try
            {
                await _webhookProvisioner.DeprovisionAsync(agentId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to deprovision outbound webhook registration for deleted agent {AgentId}.",
                    agentId);
            }
        }
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
        var window = (handle as IAgentHandleInspector)?.GetContextWindowTokens();
        return Ok(BuildContextResponse(agentId, sessionId, diag, window));
    }

    /// <summary>
    /// Builds the body of <see cref="GetContext"/>. Extracted from the action (#2795) so a test can
    /// serialize the <b>actual</b> response shape and assert the Blazor portal's
    /// <c>ContextInfoDto</c> binds to it.
    /// </summary>
    /// <remarks>
    /// The response is an anonymous type, so there is no shared contract type the WASM portal client
    /// could reference (and it could not reference one in <c>BotNexus.Domain</c> anyway - see
    /// <c>AgentConfigContracts.cs</c>). The portal therefore mirrors this shape in a private DTO, and
    /// <c>AgentConfigContractTests</c> pins the two together by calling this method. Note the token
    /// counts are <b>nested</b> under <c>sections.systemPrompt.tokens</c>: the portal originally
    /// declared a flat <c>SystemPromptTokens</c>, which silently never bound and rendered an em-dash.
    /// If you flatten or rename anything here, that test reddens - fix the portal DTO, do not relax it.
    /// <para>
    /// This is <c>public</c> rather than <c>internal</c>+<c>InternalsVisibleTo</c> on purpose (#2795):
    /// naming the SignalR BlazorClient test assembly in this csproj put a
    /// <c>BotNexus.Extensions.Channels.*</c> string inside generic gateway orchestration, which is
    /// exactly what the #2086 R1 channel-knowledge fence forbids
    /// (<c>ChannelKnowledgeFenceArchitectureTests.Rule1_GenericProjects_DoNotReferenceConcreteChannelExtensions</c>).
    /// The fence is right and must not be widened, so the helper is exposed instead.
    /// </para>
    /// <para>
    /// #3091: <paramref name="contextWindowTokens"/> is supplied by the caller from the resolved
    /// model - there is deliberately no default literal here. When it is <see langword="null"/> the
    /// window is genuinely unknown, and both it and the derived <c>usagePercent</c> are emitted as
    /// JSON <c>null</c> rather than as a plausible-looking constant, because a consumer can detect an
    /// absent number and cannot detect a wrong one.
    /// </para>
    /// </remarks>
    /// <param name="agentId">The agent the session belongs to.</param>
    /// <param name="sessionId">The session being reported on.</param>
    /// <param name="diag">The handle's context diagnostics.</param>
    /// <param name="contextWindowTokens">
    /// The resolved context window in tokens, or <see langword="null"/> when unresolvable.
    /// </param>
    public static object BuildContextResponse(
        string agentId,
        string sessionId,
        ContextDiagnostics diag,
        int? contextWindowTokens = null)
    {
        return new
        {
            agentId,
            sessionId,
            totalEstimatedTokens = diag.TotalEstimatedTokens,
            contextWindowTokens,
            usagePercent = contextWindowTokens is > 0
                ? Math.Round((double)diag.TotalEstimatedTokens / contextWindowTokens.Value * 100, 1)
                : (double?)null,
            sections = new
            {
                systemPrompt = new { tokens = diag.SystemPromptTokens, chars = diag.SystemPromptChars },
                toolDefinitions = new { tokens = diag.ToolDefinitionTokens, toolCount = diag.ToolCount },
                conversationHistory = new { tokens = diag.HistoryTokens, entryCount = diag.HistoryEntryCount }
            }
        };
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

    // Compensation: best-effort delete of a just-written config entry when a later lifecycle step
    // fails. A failure to compensate is logged but cannot itself surface a new error - the caller
    // is already returning a 500 for the primary failure.
    private async Task CompensateConfigDeleteAsync(string agentId, CancellationToken cancellationToken)
    {
        try
        {
            await _configurationWriter.DeleteAsync(agentId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback failed: could not delete persisted config for agent {AgentId} after a lifecycle failure.", agentId);
        }
    }

    // Compensation: best-effort restore of the previous descriptor to config when an update's
    // provisioning step fails after the config was already overwritten.
    private async Task CompensateConfigSaveAsync(AgentDescriptor previous, CancellationToken cancellationToken)
    {
        try
        {
            await _configurationWriter.SaveAsync(previous, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback failed: could not restore previous config for agent {AgentId} after a lifecycle failure.", previous.AgentId.Value);
        }
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
