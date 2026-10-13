using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

internal static class ClientConversationWriteGuard
{
    internal static ActionResult? Check(ControllerBase controller, Conversation? conversation, AgentId? expectedAgent = null)
    {
        var status = ConversationClientWritePolicy.Evaluate(conversation, expectedAgent);
        if (conversation is not null && !IsAgentAllowed(controller, conversation.AgentId))
            status = 404;
        return Failure(status);
    }

    internal static async Task<ActionResult?> CheckSessionAsync(ControllerBase controller, IConversationStore? store,
        GatewaySession? session, AgentId? expectedAgent = null, CancellationToken cancellationToken = default)
    {
        var status = await ConversationClientWritePolicy.EvaluateSessionAsync(store, session, expectedAgent, cancellationToken);
        if (session is not null && !IsAgentAllowed(controller, session.AgentId))
            status = 404;
        return Failure(status);
    }

    private static bool IsAgentAllowed(ControllerBase controller, AgentId agentId)
    {
        if (controller.HttpContext?.Items.TryGetValue(GatewayAuthHttpContext.CallerIdentityItemKey, out var value) != true ||
            value is not GatewayCallerIdentity identity)
            return true;
        return identity.IsAdmin || identity.AllowedAgents.Count == 0 ||
            identity.AllowedAgents.Any(allowed => string.Equals(allowed, agentId.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static ActionResult? Failure(int status) => status switch
    {
        0 => null,
        404 => new NotFoundResult(),
        _ => new ObjectResult(new { error = "Conversation is read-only." }) { StatusCode = status }
    };
}
