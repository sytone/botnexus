using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Gateway lifecycle management endpoints.
/// </summary>
[ApiController]
[Route("api/gateway")]
public sealed class GatewayController(IOptions<GatewayOptions> options) : ControllerBase
{
    private readonly GatewayOptions _options = options.Value;

    /// <summary>Returns runtime and build information about the running gateway.</summary>
    [HttpGet("info")]
    public IActionResult Info() => Ok(new
    {
        startedAt     = GatewayBuildInfo.StartedAt,
        uptimeSeconds = (long)(DateTimeOffset.UtcNow - GatewayBuildInfo.StartedAt).TotalSeconds,
        commitSha     = GatewayBuildInfo.CommitSha,
        commitShort   = GatewayBuildInfo.CommitShort,
        version       = GatewayBuildInfo.Version,
        defaultAgentId = _options.DefaultAgentId
    });
}
