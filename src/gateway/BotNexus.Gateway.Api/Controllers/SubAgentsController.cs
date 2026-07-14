using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Read-only platform-wide sub-agent observability surface (Issue #1941). Surfaces persisted
/// sub-agent runs - task lineage (parent/child agent), archetype, lifecycle status
/// (Active/Completed/Failed/Killed/TimedOut), and start/end timestamps - from the existing
/// <c>sub_agent_sessions</c> store so an operator can review what sub-agents did after the fact,
/// including whether a run genuinely completed or bailed.
/// </summary>
/// <remarks>
/// This controller is strictly read-only: it never spawns, kills, or mutates sub-agent state and
/// does not touch <c>DefaultSubAgentManager</c> write paths. It reads the same persisted rows that
/// the parent-scoped <c>GET /api/sessions/{id}/subagents/history</c> endpoint exposes, but
/// aggregated across every parent session for a single observability feed. Authenticated by
/// <c>GatewayAuthMiddleware</c> like all other <c>/api/*</c> endpoints, so no auth attributes are
/// declared here.
/// </remarks>
[ApiController]
[Route("api/subagents")]
public sealed class SubAgentsController : ControllerBase
{
    private readonly ISessionStore _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubAgentsController"/> class.
    /// </summary>
    /// <param name="sessions">The session store that owns the persisted <c>sub_agent_sessions</c> rows.</param>
    public SubAgentsController(ISessionStore sessions)
    {
        _sessions = sessions;
    }

    /// <summary>
    /// Lists persisted sub-agent runs across all parent sessions, newest-started first.
    /// </summary>
    /// <param name="status">
    /// Optional case-insensitive status filter (e.g. <c>Active</c>, <c>Completed</c>, <c>Failed</c>,
    /// <c>Killed</c>, <c>TimedOut</c>). When omitted, runs of every status are returned.
    /// </param>
    /// <param name="limit">Maximum number of rows to return (1-500, default 200).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A read-only list of sub-agent session summaries.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SubAgentSessionSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<SubAgentSessionSummary>>> List(
        [FromQuery] string? status = null,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return BadRequest(new { error = "limit must be greater than zero." });

        var boundedLimit = Math.Min(limit, 500);
        var results = await _sessions.ListAllSubAgentSessionsAsync(status, boundedLimit, cancellationToken);
        return Ok(results);
    }
}
