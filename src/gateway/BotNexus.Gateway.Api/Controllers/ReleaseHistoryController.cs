using BotNexus.Gateway.Api.ReleaseHistory;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Serves release history and running-source distance from the local checkout.</summary>
[ApiController]
[Route("api/release-history")]
public sealed class ReleaseHistoryController(LocalReleaseHistoryService service) : ControllerBase
{
    /// <summary>Gets release history; set refresh to update local remote-tracking refs first.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> Get(
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await service.GetAsync(refresh, cancellationToken));
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidDataException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
