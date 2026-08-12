using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Read-only platform-wide stats overview for the portal stats section (issue #1692). Aggregates
/// signals that already exist - active agent loops from <see cref="IActiveLoopTracker"/> and the
/// platform-wide active sub-agent tally from <see cref="ISubAgentManager.ActiveSubAgentCount"/> -
/// into a single endpoint so the portal can show live in-flight work without making the channel
/// stitch several diagnostics calls together. Only the cross-cutting aggregation is new; the
/// underlying telemetry is reused. Authenticated by <c>GatewayAuthMiddleware</c> (same as all other
/// /api/* endpoints), so no auth attributes are declared here. Both services are injected as
/// nullable-optional and the endpoint degrades to zeros when a signal is not registered, mirroring
/// the defensive pattern in <see cref="DiagnosticsController"/>.
/// </summary>
[ApiController]
[Route("api/stats")]
public sealed class StatsController(
    IActiveLoopTracker? activeLoopTracker = null,
    ISubAgentManager? subAgentManager = null) : ControllerBase
{
    private readonly IActiveLoopTracker? _activeLoopTracker = activeLoopTracker;
    private readonly ISubAgentManager? _subAgentManager = subAgentManager;

    /// <summary>
    /// Returns the platform stats overview: the live count of active agent loops (plus the peak and
    /// total-completed counters for context) and the live platform-wide active sub-agent count.
    /// Always returns 200 with zeros for any signal that is not enabled, so the portal can render a
    /// stable panel and poll it on a light interval rather than handling a 404.
    /// </summary>
    [HttpGet]
    public IActionResult GetOverview()
    {
        // #2794: ONE snapshot call. Reading ActiveCount separately from the detail list would let a
        // concurrent start/completion land between the two reads and ship a count that disagrees
        // with the rows the portal renders (AC2).
        var snapshot = _activeLoopTracker?.GetSnapshot();

        return Ok(new PlatformStatsDto
        {
            ActiveAgentLoops = snapshot?.ActiveCount ?? 0,
            PeakAgentLoops = snapshot?.PeakCount ?? 0,
            TotalCompletedLoops = snapshot?.TotalCompleted ?? 0,
            ActiveSubAgents = _subAgentManager?.ActiveSubAgentCount ?? 0,
            ActiveLoopDetails = snapshot is null
                ? []
                : [.. snapshot.ActiveLoops.Select(l => new ActiveLoopDetailDto
                {
                    LoopId = l.LoopId,
                    AgentId = l.AgentId,
                    ConversationId = l.ConversationId,
                    SessionId = l.SessionId,
                    StartedAtUtc = l.StartedAtUtc
                })]
        });
    }
}

/// <summary>
/// Platform-wide stats headline numbers surfaced by the portal stats section. Active counts reflect
/// real in-flight work: a running agent loop or sub-agent increments its count and drops out when it
/// finishes.
/// </summary>
public sealed class PlatformStatsDto
{
    /// <summary>Live number of agent loops currently executing across the platform.</summary>
    public required int ActiveAgentLoops { get; init; }

    /// <summary>Peak concurrent agent-loop count observed since gateway startup.</summary>
    public required int PeakAgentLoops { get; init; }

    /// <summary>Total number of agent loops that have completed since gateway startup.</summary>
    public required long TotalCompletedLoops { get; init; }

    /// <summary>Live number of sub-agents currently running across all parent sessions.</summary>
    public required int ActiveSubAgents { get; init; }

    /// <summary>
    /// One row per active agent loop, taken from the same snapshot as <see cref="ActiveAgentLoops"/>
    /// so the headline count always equals this collection's size.
    /// </summary>
    public required IReadOnlyList<ActiveLoopDetailDto> ActiveLoopDetails { get; init; }
}

/// <summary>
/// Operator-facing detail for a single in-flight agent loop: who is running, in which conversation
/// and session, and since when. Enough to decide whether a gateway restart is safe.
/// </summary>
public sealed class ActiveLoopDetailDto
{
    /// <summary>Opaque run identity, stable for the lifetime of the loop.</summary>
    public required string LoopId { get; init; }

    /// <summary>Agent executing the loop, when known.</summary>
    public string? AgentId { get; init; }

    /// <summary>Conversation the loop belongs to; drives portal addressability when present.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Session the loop is running under, when known.</summary>
    public string? SessionId { get; init; }

    /// <summary>UTC instant the loop started, used by the portal to derive run age.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }
}
