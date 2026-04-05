using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for session management — listing, inspecting, and deleting sessions.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class SessionsController : ControllerBase
{
    private readonly ISessionStore _sessions;

    public SessionsController(ISessionStore sessions) => _sessions = sessions;

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

    /// <summary>Deletes a session.</summary>
    [HttpDelete("{sessionId}")]
    public async Task<ActionResult> Delete(string sessionId, CancellationToken cancellationToken)
    {
        await _sessions.DeleteAsync(sessionId, cancellationToken);
        return NoContent();
    }
}
