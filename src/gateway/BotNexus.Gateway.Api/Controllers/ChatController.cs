using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST endpoint for non-streaming chat. For real-time streaming, use the WebSocket endpoint.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ChatController : ControllerBase
{
    private readonly IAgentSupervisor _supervisor;
    private readonly ISessionStore _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatController"/> class.
    /// </summary>
    /// <param name="supervisor">The agent supervisor for managing agent instances.</param>
    /// <param name="sessions">The session store for persisting conversation history.</param>
    public ChatController(IAgentSupervisor supervisor, ISessionStore sessions)
    {
        _supervisor = supervisor;
        _sessions = sessions;
    }

    /// <summary>
    /// Sends a message to an agent and returns the complete response.
    /// For streaming, connect via <c>ws://host/ws</c>.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Send([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId))
            return BadRequest(new { error = "agentId is required." });

        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required." });

        try
        {
            var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
                ? Guid.NewGuid().ToString("N")
                : request.SessionId;
            var handle = await _supervisor.GetOrCreateAsync(request.AgentId, sessionId, cancellationToken);
            var response = await handle.PromptAsync(request.Message, cancellationToken);

            var session = await _sessions.GetOrCreateAsync(sessionId, request.AgentId, cancellationToken);
            session.AddEntry(new SessionEntry { Role = "user", Content = request.Message });
            session.AddEntry(new SessionEntry { Role = "assistant", Content = response.Content });
            await _sessions.SaveAsync(session, cancellationToken);

            return Ok(new ChatResponse(sessionId, response.Content, response.Usage));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (AgentConcurrencyLimitExceededException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Injects a steering message into an active agent run.
    /// </summary>
    [HttpPost("steer")]
    public async Task<IActionResult> Steer([FromBody] AgentControlRequest request, CancellationToken cancellationToken)
    {
        var instance = _supervisor.GetInstance(request.AgentId, request.SessionId);
        if (instance is null)
            return NotFound(new { message = "Agent session not found." });

        try
        {
            var handle = await _supervisor.GetOrCreateAsync(request.AgentId, request.SessionId, cancellationToken);
            await handle.SteerAsync(request.Message, cancellationToken);
            return Accepted();
        }
        catch (AgentConcurrencyLimitExceededException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Queues a follow-up message for an active agent session.
    /// </summary>
    [HttpPost("follow-up")]
    public async Task<IActionResult> FollowUp([FromBody] AgentControlRequest request, CancellationToken cancellationToken)
    {
        var instance = _supervisor.GetInstance(request.AgentId, request.SessionId);
        if (instance is null)
            return NotFound(new { message = "Agent session not found." });

        try
        {
            var handle = await _supervisor.GetOrCreateAsync(request.AgentId, request.SessionId, cancellationToken);
            await handle.FollowUpAsync(request.Message, cancellationToken);
            return Accepted();
        }
        catch (AgentConcurrencyLimitExceededException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
    }
}

/// <summary>Chat request payload.</summary>
public sealed record ChatRequest(string AgentId, string Message, string? SessionId = null);

/// <summary>Agent control request payload.</summary>
public sealed record AgentControlRequest(string AgentId, string SessionId, string Message);

/// <summary>Chat response payload.</summary>
public sealed record ChatResponse(string SessionId, string Content, AgentResponseUsage? Usage = null);
