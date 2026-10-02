using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Gateway lifecycle management endpoints.
/// </summary>
[ApiController]
[Route("api/gateway")]
public sealed class GatewayController(
    IOptions<GatewayOptions> options,
    IHostApplicationLifetime? hostLifetime = null) : ControllerBase
{
    private readonly GatewayOptions _options = options.Value;
    private readonly IHostApplicationLifetime? _hostLifetime = hostLifetime;

    /// <summary>Returns runtime and build information about the running gateway.</summary>
    [HttpGet("info")]
    public IActionResult Info() => Ok(new
    {
        startedAt     = GatewayBuildInfo.StartedAt,
        uptimeSeconds = (long)(DateTimeOffset.UtcNow - GatewayBuildInfo.StartedAt).TotalSeconds,
        buildTimestamp = GatewayBuildInfo.BuildTimestamp,
        commitSha     = GatewayBuildInfo.CommitSha,
        commitShort   = GatewayBuildInfo.CommitShort,
        version       = GatewayBuildInfo.Version,
        defaultAgentId = _options.DefaultAgentId
    });

    /// <summary>
    /// Requests an orderly host shutdown for a planned CLI stop, restart, or update. The request is
    /// accepted before the lifetime signal is raised so the caller receives an acknowledgement
    /// instead of racing Kestrel teardown.
    /// </summary>
    [HttpPost("shutdown")]
    public IActionResult Shutdown()
    {
        if (_hostLifetime is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        HttpContext.Response.OnCompleted(() =>
        {
            _hostLifetime.StopApplication();
            return Task.CompletedTask;
        });

        return Accepted(new { status = "shutdown-requested" });
    }
}
