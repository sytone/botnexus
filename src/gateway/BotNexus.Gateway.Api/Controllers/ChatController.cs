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
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var session = await _sessions.GetOrCreateAsync(sessionId, request.AgentId, cancellationToken);

        session.AddEntry(new SessionEntry { Role = "user", Content = request.Message });

        var handle = await _supervisor.GetOrCreateAsync(request.AgentId, sessionId, cancellationToken);
        var response = await handle.PromptAsync(request.Message, cancellationToken);

        session.AddEntry(new SessionEntry { Role = "assistant", Content = response.Content });
        await _sessions.SaveAsync(session, cancellationToken);

        return Ok(new ChatResponse(sessionId, response.Content, response.Usage));
    }
}

/// <summary>Chat request payload.</summary>
public sealed record ChatRequest(string AgentId, string Message, string? SessionId = null);

/// <summary>Chat response payload.</summary>
public sealed record ChatResponse(string SessionId, string Content, AgentResponseUsage? Usage = null);
