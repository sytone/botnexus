using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for session management — listing, inspecting, and deleting sessions.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class SessionsController : ControllerBase
{
    private readonly ISessionStore _sessions;
    private readonly ISubAgentManager _subAgentManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionsController"/> class.
    /// </summary>
    /// <param name="sessions">The session store for managing conversation sessions.</param>
    /// <param name="subAgentManager">Sub-agent manager for session-scoped sub-agent lifecycle operations.</param>
    public SessionsController(ISessionStore sessions, ISubAgentManager? subAgentManager = null)
    {
        _sessions = sessions;
        _subAgentManager = subAgentManager ?? NoOpSubAgentManager.Instance;
    }

    /// <summary>Lists sessions, optionally filtered by agent ID.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GatewaySession>>> List([FromQuery] string? agentId, CancellationToken cancellationToken)
        => Ok(await _sessions.ListAsync(agentId, cancellationToken));

    /// <summary>Gets a specific session by ID.</summary>
    [HttpGet("{sessionId}")]
    public async Task<ActionResult<GatewaySession>> Get(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        return session is not null ? Ok(session) : NotFound();
    }

    /// <summary>Lists sub-agents for a specific session.</summary>
    [HttpGet("{sessionId}/subagents")]
    [ProducesResponseType(typeof(IReadOnlyList<SubAgentInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<SubAgentInfo>>> ListSubAgents(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return NotFound();

        var subAgents = await _subAgentManager.ListAsync(sessionId, cancellationToken);
        return Ok(subAgents);
    }

    /// <summary>Kills a sub-agent owned by the specified session.</summary>
    [HttpDelete("{sessionId}/subagents/{subAgentId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> KillSubAgent(string sessionId, string subAgentId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return NotFound();

        var subAgent = await _subAgentManager.GetAsync(subAgentId, cancellationToken);
        if (subAgent is null)
            return NotFound();

        if (!string.Equals(subAgent.ParentSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sub-agent does not belong to the requested session." });

        var killed = await _subAgentManager.KillAsync(subAgentId, sessionId, cancellationToken);
        if (!killed)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Gets paginated session history for long-running conversations.
    /// </summary>
    [HttpGet("{sessionId}/history")]
    public async Task<ActionResult<SessionHistoryResponse>> GetHistory(
        string sessionId,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
            return BadRequest(new { error = "offset must be greater than or equal to zero." });

        if (limit <= 0)
            return BadRequest(new { error = "limit must be greater than zero." });

        var boundedLimit = Math.Min(limit, 200);

        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return NotFound();

        var totalCount = session.History.Count;
        var entries = session.GetHistorySnapshot(offset, boundedLimit);
        return Ok(new SessionHistoryResponse(offset, boundedLimit, totalCount, entries));
    }

    /// <summary>
    /// Gets metadata for a specific session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session metadata key-value pairs.</returns>
    [HttpGet("{sessionId}/metadata")]
    [ProducesResponseType(typeof(Dictionary<string, object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Dictionary<string, object?>>> GetMetadata(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return NotFound();

        var authorizationFailure = AuthorizeSessionCaller(session);
        if (authorizationFailure is not null)
            return authorizationFailure;

        return Ok(session.Metadata);
    }

    /// <summary>
    /// Merges metadata entries into a specific session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="metadataPatch">JSON object containing metadata keys to add/update. Keys with null values are removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated session metadata key-value pairs.</returns>
    [HttpPatch("{sessionId}/metadata")]
    [ProducesResponseType(typeof(Dictionary<string, object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Dictionary<string, object?>>> PatchMetadata(
        string sessionId,
        [FromBody] JsonElement metadataPatch,
        CancellationToken cancellationToken)
    {
        if (metadataPatch.ValueKind != JsonValueKind.Object)
            return BadRequest(new { error = "Metadata patch body must be a JSON object." });

        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return NotFound();

        var authorizationFailure = AuthorizeSessionCaller(session);
        if (authorizationFailure is not null)
            return authorizationFailure;

        foreach (var property in metadataPatch.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                session.Metadata.Remove(property.Name);
                continue;
            }

            session.Metadata[property.Name] = ConvertJsonElement(property.Value);
        }

        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _sessions.SaveAsync(session, cancellationToken);
        return Ok(session.Metadata);
    }

    private ObjectResult? AuthorizeSessionCaller(GatewaySession session)
    {
        var items = HttpContext?.Items;
        if (items is not null &&
            items.TryGetValue(GatewayAuthMiddleware.CallerIdentityItemKey, out var identityValue) &&
            identityValue is GatewayCallerIdentity identity &&
            !string.IsNullOrWhiteSpace(identity.CallerId) &&
            !string.Equals(identity.CallerId, session.CallerId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Caller is not authorized for this session." });
        }

        return null;
    }

    /// <summary>Deletes a session.</summary>
    [HttpDelete("{sessionId}")]
    public async Task<ActionResult> Delete(string sessionId, CancellationToken cancellationToken)
    {
        await _sessions.DeleteAsync(sessionId, cancellationToken);
        return NoContent();
    }

    /// <summary>Suspends an active session.</summary>
    [HttpPatch("{sessionId}/suspend")]
    public async Task<ActionResult<GatewaySession>> Suspend(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return NotFound();

        if (session.Status != SessionStatus.Active)
            return Conflict(new { error = $"Cannot suspend session in '{session.Status}' state." });

        session.Status = SessionStatus.Suspended;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _sessions.SaveAsync(session, cancellationToken);
        return Ok(session);
    }

    /// <summary>Resumes a suspended session.</summary>
    [HttpPatch("{sessionId}/resume")]
    public async Task<ActionResult<GatewaySession>> Resume(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return NotFound();

        if (session.Status != SessionStatus.Suspended)
            return Conflict(new { error = $"Cannot resume session in '{session.Status}' state." });

        session.Status = SessionStatus.Active;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _sessions.SaveAsync(session, cancellationToken);
        return Ok(session);
    }

    private static object? ConvertJsonElement(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => ConvertJsonElement(property.Value)),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElement)
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var intValue) => intValue,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.Null => null,
            _ => null
        };

    private sealed class NoOpSubAgentManager : ISubAgentManager
    {
        public static readonly NoOpSubAgentManager Instance = new();

        public Task<SubAgentInfo> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Sub-agent spawning is not supported by this controller instance.");

        public Task<IReadOnlyList<SubAgentInfo>> ListAsync(string parentSessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SubAgentInfo>>([]);

        public Task<SubAgentInfo?> GetAsync(string subAgentId, CancellationToken ct = default)
            => Task.FromResult<SubAgentInfo?>(null);

        public Task<bool> KillAsync(string subAgentId, string requestingSessionId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task OnCompletedAsync(string subAgentId, string resultSummary, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
