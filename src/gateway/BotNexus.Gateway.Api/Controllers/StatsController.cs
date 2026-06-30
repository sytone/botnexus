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
        return Ok(new PlatformStatsDto
        {
            ActiveAgentLoops = _activeLoopTracker?.ActiveCount ?? 0,
            PeakAgentLoops = _activeLoopTracker?.PeakCount ?? 0,
            TotalCompletedLoops = _activeLoopTracker?.TotalCompleted ?? 0,
            ActiveSubAgents = _subAgentManager?.ActiveSubAgentCount ?? 0
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
}
