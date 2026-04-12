using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Federation;
using Microsoft.AspNetCore.Mvc;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Represents cross world federation controller.
/// </summary>
/// <param name="registry">The agent registry.</param>
/// <param name="supervisor">The agent supervisor.</param>
/// <param name="sessionStore">The session store.</param>
/// <param name="inboundAuthService">The inbound authentication service.</param>
/// <param name="platformConfig">The platform configuration.</param>
[ApiController]
[Route("api/federation/cross-world")]
public sealed class CrossWorldFederationController(
    IAgentRegistry registry,
    IAgentSupervisor supervisor,
    ISessionStore sessionStore,
    CrossWorldInboundAuthService inboundAuthService,
    PlatformConfig platformConfig) : ControllerBase
{
    private readonly string _localWorldId = WorldIdentityResolver.Resolve(platformConfig).Id;

    /// <summary>
    /// Executes relay async.
    /// </summary>
    /// <returns>The relay async result.</returns>
    [HttpPost("relay")]
    public async Task<ActionResult<CrossWorldRelayResponse>> RelayAsync(
        [FromBody] CrossWorldRelayRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourceWorldId))
            return BadRequest(new { error = "sourceWorldId is required." });
        if (string.IsNullOrWhiteSpace(request.SourceAgentId))
            return BadRequest(new { error = "sourceAgentId is required." });
        if (string.IsNullOrWhiteSpace(request.TargetAgentId))
            return BadRequest(new { error = "targetAgentId is required." });
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required." });

        var targetAgentId = AgentId.From(request.TargetAgentId);
        if (!registry.Contains(targetAgentId))
            return NotFound(new { error = $"Target agent '{request.TargetAgentId}' is not registered." });

        var presentedApiKey = Request.Headers.TryGetValue("X-Cross-World-Key", out var keyHeader)
            ? keyHeader.ToString()
            : null;
        if (!inboundAuthService.TryAuthorize(request.SourceWorldId, targetAgentId, presentedApiKey, out var authError))
            return Unauthorized(new { error = authError });

        var remoteSessionId = string.IsNullOrWhiteSpace(request.RemoteSessionId)
            ? SessionId.ForAgentConversation(
                AgentId.From($"{request.SourceWorldId}:{request.SourceAgentId}"),
                targetAgentId,
                Guid.NewGuid().ToString("N"))
            : SessionId.From(request.RemoteSessionId);

        var session = await sessionStore.GetOrCreateAsync(remoteSessionId, targetAgentId, cancellationToken).ConfigureAwait(false);
        session.SessionType = SessionType.AgentAgent;
        session.ChannelType = ChannelKey.From("cross-world");
        session.CallerId = null;
        session.Participants.Clear();
        session.Participants.Add(new SessionParticipant
        {
            Type = ParticipantType.Agent,
            Id = request.SourceAgentId,
            WorldId = request.SourceWorldId,
            Role = "initiator"
        });
        session.Participants.Add(new SessionParticipant
        {
            Type = ParticipantType.Agent,
            Id = targetAgentId.Value,
            WorldId = _localWorldId,
            Role = "target"
        });
        session.Metadata["conversationId"] = request.ConversationId;
        session.Metadata["sourceWorldId"] = request.SourceWorldId;
        session.Metadata["sourceSessionId"] = request.SourceSessionId;
        session.Status = GatewaySessionStatus.Active;
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = request.Message
        });

        var handle = await supervisor.GetOrCreateAsync(targetAgentId, remoteSessionId, cancellationToken).ConfigureAwait(false);
        var response = await handle.PromptAsync(request.Message, cancellationToken).ConfigureAwait(false);
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = response.Content ?? string.Empty
        });
        await sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        return Ok(new CrossWorldRelayResponse
        {
            Response = response.Content ?? string.Empty,
            Status = "active",
            SessionId = remoteSessionId.Value
        });
    }
}
