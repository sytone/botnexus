using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Gateway lifecycle management endpoints.
/// </summary>
/// <summary>
/// Represents gateway controller.
/// </summary>
[ApiController]
[Route("api/gateway")]
public sealed class GatewayController(IHostApplicationLifetime lifetime) : ControllerBase
{
    /// <summary>
    /// Initiates a graceful shutdown of the gateway process.
    /// The process supervisor (systemd, Docker, dev-loop script, etc.) is expected to restart it.
    /// </summary>
    /// <summary>
    /// Executes shutdown.
    /// </summary>
    /// <returns>The shutdown result.</returns>
    [HttpPost("shutdown")]
    public IActionResult Shutdown()
    {
        lifetime.StopApplication();
        return Ok(new { status = "shutting_down" });
    }
}
