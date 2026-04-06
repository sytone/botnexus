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

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionsController"/> class.
    /// </summary>
    /// <param name="sessions">The session store for managing conversation sessions.</param>
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

    /// <summary>
    /// Gets paginated session history for long-running conversations.
    /// </summary>
    [HttpGet("{sessionId}/history")]
    public async Task<ActionResult<SessionHistoryResponse>> GetHistory(
        string sessionId,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
            return BadRequest(new { error = "offset must be greater than or equal to zero." });

        if (limit <= 0)
            return BadRequest(new { error = "limit must be greater than zero." });

        var boundedLimit = Math.Min(limit, 200);

        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return NotFound();

        var totalCount = session.History.Count;
        var entries = session.GetHistorySnapshot(offset, boundedLimit);
        return Ok(new SessionHistoryResponse(offset, boundedLimit, totalCount, entries));
    }

    /// <summary>Deletes a session.</summary>
    [HttpDelete("{sessionId}")]
    public async Task<ActionResult> Delete(string sessionId, CancellationToken cancellationToken)
    {
        await _sessions.DeleteAsync(sessionId, cancellationToken);
        return NoContent();
    }

    /// <summary>Suspends an active session.</summary>
    [HttpPatch("{sessionId}/suspend")]
    public async Task<ActionResult<GatewaySession>> Suspend(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return NotFound();

        if (session.Status != SessionStatus.Active)
            return Conflict(new { error = $"Cannot suspend session in '{session.Status}' state." });

        session.Status = SessionStatus.Suspended;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _sessions.SaveAsync(session, cancellationToken);
        return Ok(session);
    }

    /// <summary>Resumes a suspended session.</summary>
    [HttpPatch("{sessionId}/resume")]
    public async Task<ActionResult<GatewaySession>> Resume(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return NotFound();

        if (session.Status != SessionStatus.Suspended)
            return Conflict(new { error = $"Cannot resume session in '{session.Status}' state." });

        session.Status = SessionStatus.Active;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _sessions.SaveAsync(session, cancellationToken);
        return Ok(session);
    }
}

/// <summary>
/// Represents paginated session history response containing conversation entries.
/// </summary>
/// <param name="Offset">The zero-based offset for pagination.</param>
/// <param name="Limit">The maximum number of entries returned.</param>
/// <param name="TotalCount">The total number of entries in the session history.</param>
/// <param name="Entries">The list of session history entries.</param>
public sealed record SessionHistoryResponse(
    int Offset,
    int Limit,
    int TotalCount,
    IReadOnlyList<SessionEntry> Entries);
