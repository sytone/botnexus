using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Gateway lifecycle management endpoints.
/// </summary>
[ApiController]
[Route("api/gateway")]
public sealed class GatewayController(IHostApplicationLifetime lifetime) : ControllerBase
{
    /// <summary>Returns runtime and build information about the running gateway.</summary>
    [HttpGet("info")]
    public IActionResult Info() => Ok(new
    {
        startedAt     = GatewayBuildInfo.StartedAt,
        uptimeSeconds = (long)(DateTimeOffset.UtcNow - GatewayBuildInfo.StartedAt).TotalSeconds,
        commitSha     = GatewayBuildInfo.CommitSha,
        commitShort   = GatewayBuildInfo.CommitShort,
        version       = GatewayBuildInfo.Version
    });

    /// <summary>
    /// Initiates a graceful shutdown so the process supervisor can restart the gateway cleanly.
    /// </summary>
    /// <returns>A shutdown acknowledgement payload.</returns>
    [HttpPost("shutdown")]
    public IActionResult Shutdown()
    {
        lifetime.StopApplication();
        return Ok(new { status = "shutting_down" });
    }
}
