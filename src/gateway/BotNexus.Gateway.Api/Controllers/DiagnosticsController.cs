using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Lightweight diagnostics endpoint for channel-side error reports.
/// Authenticated by <see cref="GatewayAuthMiddleware"/> (same as all other /api/* endpoints).
/// </summary>
[ApiController]
[Route("api/diagnostics")]
public sealed class DiagnosticsController(ILogger<DiagnosticsController> logger) : ControllerBase
{
    private readonly ILogger<DiagnosticsController> _logger = logger;

    /// <summary>
    /// Accepts an error report from any channel adapter and logs it at Error level.
    /// Authentication is enforced by <see cref="GatewayAuthMiddleware"/> for all /api/* paths.
    /// </summary>
    [HttpPost("channel-error")]
    public IActionResult ReportChannelError([FromBody] ChannelErrorReport report)
    {
        if (report is null)
            return BadRequest("Report body is required.");

        _logger.LogError(
            "Channel error reported. Agent={AgentId} Session={SessionId} Url={Url} UserAgent={UserAgent} Timestamp={Timestamp} Message={Message}\nComponentStack={ComponentStack}\nStackTrace={StackTrace}",
            report.AgentId,
            report.SessionId,
            report.Url,
            report.UserAgent,
            report.Timestamp,
            report.Message,
            report.ComponentStack,
            report.StackTrace);

        return Ok();
    }
}
