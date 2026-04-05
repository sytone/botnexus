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
    public async Task<ActionResult<IReadOnlyList<GatewaySession>>> List([FromQuery] string? agentId, CancellationToken ct)
        => Ok(await _sessions.ListAsync(agentId, ct));

    /// <summary>Gets a specific session by ID.</summary>
    [HttpGet("{sessionId}")]
    public async Task<ActionResult<GatewaySession>> Get(string sessionId, CancellationToken ct)
    {
        var session = await _sessions.GetAsync(sessionId, ct);
        return session is not null ? Ok(session) : NotFound();
    }

    /// <summary>Deletes a session.</summary>
    [HttpDelete("{sessionId}")]
    public async Task<ActionResult> Delete(string sessionId, CancellationToken ct)
    {
        await _sessions.DeleteAsync(sessionId, ct);
        return NoContent();
    }
}
