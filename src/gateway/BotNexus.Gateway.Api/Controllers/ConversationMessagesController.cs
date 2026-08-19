using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// First-class HTTP door onto the conversation-addressed inbound path (issue #2840):
/// <c>POST /api/agents/{agentId}/conversations/{conversationId}/messages</c> posts a message into an
/// existing conversation and, by default, lets the agent take a turn on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="IInboundMessageOrchestrator"/> is the only component that
/// delivers a <em>conversation-addressed</em> message, and until now the only HTTP surface holding it
/// was <c>WebhookInboundController</c> - which requires per-caller webhook registration plus HMAC
/// provisioning. That is the right shape for untrusted third-party callbacks and the wrong shape for a
/// local script that already has gateway credentials. <c>POST /api/chat</c> is not a substitute: it is
/// <em>session</em>-scoped, always blocks on a turn, and mints a random session when none is supplied,
/// so a repeat caller silently accumulates orphan sessions instead of continuing one thread.
/// </para>
/// <para>
/// <b>Auth.</b> This route carries <b>no</b> special case. It is guarded by
/// <c>GatewayAuthMiddleware</c> through <c>IGatewayAuthHandler</c> like every other <c>/api/*</c> path,
/// and it is deliberately absent from <c>ShouldSkipAuth</c>. An origin-based (e.g. loopback) bypass on
/// a write endpoint that can make an agent <em>act</em> is exactly the wrong trade. Because the agent
/// id is a route segment, the middleware's existing per-agent scoping
/// (<c>IsAgentAuthorized</c> over <c>GatewayCallerIdentity.AllowedAgents</c>) applies for free: a key
/// scoped to agent A cannot post into agent B's conversation.
/// </para>
/// <para>
/// <b>Not a new delivery mechanism.</b> The wake path builds the same <see cref="InboundMessage"/>
/// shape <c>WebhookInboundController.ExecuteAgentAsync</c> already builds and hands it to the same
/// orchestrator. The append-only path resolves its session through the same
/// <see cref="IConversationDispatcher"/> seam the orchestrator reaches through, so the two modes
/// cannot drift on <em>where</em> a message is stored (the #2839 failure mode).
/// </para>
/// </remarks>
/// <param name="conversations">Conversation store, used to verify the route conversation exists and is owned by the route agent.</param>
/// <param name="sessions">Session store, used for the append-only write and to report the resolved session.</param>
/// <param name="dispatcher">Conversation-to-session resolution seam shared with the orchestrator.</param>
/// <param name="orchestrator">Inbound entry point that runs the agent turn.</param>
/// <param name="agents">Agent registry, used to distinguish an unknown agent (404) from a malformed body (400).</param>
/// <param name="logger">Logger.</param>
[ApiController]
[Route("api/agents/{agentId}/conversations/{conversationId}/messages")]
public sealed class ConversationMessagesController(
    IConversationStore conversations,
    ISessionStore sessions,
    IConversationDispatcher dispatcher,
    IInboundMessageOrchestrator orchestrator,
    IAgentRegistry agents,
    ILogger<ConversationMessagesController> logger) : ControllerBase
{
    /// <summary>The channel this endpoint's messages originate from, so history can attribute them.</summary>
    private static readonly ChannelKey ApiChannel = ChannelKey.From("api");

    /// <summary>
    /// Posts a message into an existing conversation.
    /// </summary>
    /// <remarks>
    /// Returns <c>202 Accepted</c> rather than blocking on the agent's reply: a fire-and-forget script -
    /// the common case - should not pay for the turn's latency, and a caller that wants the reply can
    /// poll <c>GET /api/conversations/{conversationId}/history</c> using the returned identifiers.
    /// </remarks>
    /// <param name="agentId">Owning agent, from the route. Also the unit of auth scoping.</param>
    /// <param name="conversationId">Existing conversation to post into, from the route. This endpoint never creates one.</param>
    /// <param name="request">Message body: the text, whether to wake the agent, and an optional caller attribution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>202 Accepted</c> with the resolved conversation and session ids; <c>400 Bad Request</c> for a
    /// malformed body; <c>404 Not Found</c> when the agent or conversation does not exist (or the
    /// conversation is not the named agent's); <c>409 Conflict</c> when an append-only write could not
    /// land.
    /// </returns>
    [HttpPost]
    [ProducesResponseType(typeof(PostConversationMessageResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PostMessage(
        string agentId,
        string conversationId,
        [FromBody] PostConversationMessageRequest? request,
        CancellationToken cancellationToken)
    {
        // Body validation first so a malformed request is a 400 the caller can distinguish from the
        // 404s below (acceptance clause 4). A blank message must never be delivered as an empty turn.
        if (request is null || string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required." });

        if (string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "agentId is required." });

        var typedAgentId = AgentId.From(agentId);
        if (!agents.Contains(typedAgentId))
            return NotFound(new { error = $"Agent '{agentId}' not found." });

        if (string.IsNullOrWhiteSpace(conversationId))
            return BadRequest(new { error = "conversationId is required." });

        var typedConversationId = ConversationId.From(conversationId);
        var conversation = await conversations.GetAsync(typedConversationId, cancellationToken);

        // A conversation owned by a DIFFERENT agent is reported as absent, not as a 403. The route pair
        // must be coherent, and "exists, but not yours" would let a caller probe for other agents'
        // conversation ids through a route their key is otherwise authorized for.
        if (conversation is null || conversation.AgentId != typedAgentId)
            return NotFound(new { error = $"Conversation '{conversationId}' not found for agent '{agentId}'." });

        var content = request.Message!.Trim();
        var senderId = BuildSenderId(request.Sender);
        var inbound = BuildInboundMessage(typedAgentId, typedConversationId, content, senderId, request.Delivery);

        if (!request.Wake)
            return await AppendWithoutWakingAsync(inbound, typedAgentId, content, senderId, cancellationToken);

        return await WakeAsync(inbound, typedAgentId, typedConversationId, cancellationToken);
    }

    /// <summary>
    /// Wake path: resolve the conversation's session, then hand the message to the orchestrator on a
    /// detached token so the 202 does not wait for the turn.
    /// </summary>
    /// <remarks>
    /// Resolution runs BEFORE the hand-off because the response contract (clause 7) promises the
    /// resolved session id, and <see cref="IInboundMessageOrchestrator.Post"/> is fire-and-forget and
    /// reports nothing back. Resolving through <see cref="IConversationDispatcher"/> - the same seam the
    /// orchestrator uses internally - means the id reported here is the id the turn will run on.
    /// </remarks>
    private async Task<IActionResult> WakeAsync(
        InboundMessage inbound,
        AgentId agentId,
        ConversationId conversationId,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(inbound, agentId, cancellationToken);

        // AcceptAsync awaits the whole turn; this endpoint must not. Detached from the request's token
        // so a client disconnect after the 202 cannot kill an agent run that has already been promised.
        _ = Task.Run(async () =>
        {
            try
            {
                await orchestrator.AcceptAsync(inbound, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Conversation message delivery failed for agent '{AgentId}' conversation '{ConversationId}'.",
                    agentId.Value, conversationId.Value);
            }
        }, CancellationToken.None);

        logger.LogInformation(
            "Accepted conversation message for agent '{AgentId}' conversation '{ConversationId}' from '{SenderId}' (wake).",
            agentId.Value, conversationId.Value, inbound.SenderId);

        return Accepted(new PostConversationMessageResponse(
            conversationId.Value, resolution.SessionId.Value, Wake: true));
    }

    /// <summary>
    /// Append-only path (<c>wake:false</c>): persist the message on the conversation's bound session
    /// without scheduling a turn.
    /// </summary>
    /// <remarks>
    /// Uses the narrow <see cref="ISessionStore.AppendEntriesAsync"/> rather than a read-mutate-save of
    /// the whole aggregate (#2132), so this write cannot roll back a concurrent metadata patch or
    /// lifecycle transition. A refused append must NOT return 202: a success receipt for a write that
    /// did not land leaves the caller with no signal to retry - the #2839 lesson.
    /// </remarks>
    private async Task<IActionResult> AppendWithoutWakingAsync(
        InboundMessage inbound,
        AgentId agentId,
        string content,
        string senderId,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(inbound, agentId, cancellationToken);

        var appended = await sessions.AppendEntriesAsync(
            resolution.SessionId,
            [new SessionEntry { Role = MessageRole.User, Content = content, SenderId = senderId }],
            cancellationToken);

        if (appended.Outcome != SessionMutationOutcome.Applied)
        {
            logger.LogWarning(
                "Append-only conversation message refused for agent '{AgentId}' session '{SessionId}': {Outcome}.",
                agentId.Value, resolution.SessionId.Value, appended.Outcome);

            return appended.Outcome == SessionMutationOutcome.NotFound
                ? NotFound(new { error = "The conversation's session no longer exists." })
                : Conflict(new { error = "The conversation's session cannot accept new messages." });
        }

        return Accepted(new PostConversationMessageResponse(
            resolution.ConversationId.Value, resolution.SessionId.Value, Wake: false));
    }

    /// <summary>
    /// Resolves the conversation's bound session through the dispatcher seam shared with the
    /// orchestrator, so both modes agree on <em>where</em> the message belongs.
    /// </summary>
    private async Task<ConversationSessionResolution> ResolveAsync(
        InboundMessage inbound, AgentId agentId, CancellationToken cancellationToken)
    {
        var context = InboundMessageContext.FromInboundMessage(agentId, inbound);
        var dispatch = await dispatcher.DispatchAsync(context, cancellationToken);
        return dispatch.Resolution;
    }

    /// <summary>
    /// Builds the inbound message. This is byte-for-byte the shape
    /// <c>WebhookInboundController.ExecuteAgentAsync</c> builds, differing only in channel and sender:
    /// routing is expressed entirely through <see cref="InboundMessageRoutingHints"/>.
    /// </summary>
    /// <remarks>
    /// The delivery mode rides in the routing hints rather than in a separate argument to the
    /// orchestrator, because it is the same class of information: what the transport is asking the
    /// gateway to do with this message. The gateway still decides whether the request is honourable
    /// (#3028) — this only states the intent.
    /// </remarks>
    private static InboundMessage BuildInboundMessage(
        AgentId agentId, ConversationId conversationId, string content, string senderId,
        InboundDeliveryMode delivery = InboundDeliveryMode.Auto)
        => new()
        {
            ChannelType = ApiChannel,
            SenderId = senderId,
            Sender = CitizenId.Of(agentId),
            ChannelAddress = ChannelAddress.From(conversationId.Value),
            Content = content,
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: agentId,
                RequestedSessionId: null,
                RequestedConversationId: conversationId,
                DeliveryMode: delivery),
            Metadata = new Dictionary<string, object?>
            {
                ["apiSender"] = senderId
            }
        };

    /// <summary>
    /// Namespaces the caller-supplied attribution under <c>api</c> so a history reader can tell the
    /// transport apart from the caller's own label, and so a caller cannot forge a <c>webhook:</c> or
    /// <c>subagent:</c> prefix by choosing its <c>sender</c> string.
    /// </summary>
    /// <returns><c>api:{sender}</c> when a sender was supplied; otherwise the bare <c>api</c> origin.</returns>
    private static string BuildSenderId(string? sender)
        => string.IsNullOrWhiteSpace(sender) ? "api" : $"api:{sender.Trim()}";
}

/// <summary>
/// Request body for <c>POST /api/agents/{agentId}/conversations/{conversationId}/messages</c>.
/// </summary>
/// <param name="Message">The message text. Required; blank is rejected with 400.</param>
/// <param name="Wake">
/// <see langword="true"/> (the default) schedules an agent turn on the message.
/// <see langword="false"/> appends it to the conversation's session for history and audit without
/// running the agent.
/// </param>
/// <param name="Sender">
/// Optional caller attribution recorded as provenance in conversation history - e.g.
/// <c>cron:pr-doctor</c>. Stored namespaced as <c>api:{sender}</c>. Display text only: it is
/// caller-supplied and is never an authorization input.
/// </param>
/// <param name="Delivery">
/// Delivery semantics requested for this message (#3028 AC4). Defaults to
/// <see cref="InboundDeliveryMode.Auto"/>, which <b>always queues</b>: the message waits for the
/// agent's current turn to finish and then takes a turn of its own. It never interrupts a running
/// turn. Pass <c>steer</c> to inject into a turn already in flight, or <c>interrupt</c> to abort the
/// running turn and redirect it. Both fall back to queueing when no turn is running — they are
/// requests, not guarantees. Ignored when <see cref="Wake"/> is <see langword="false"/>, since an
/// append-only write schedules no turn at all.
/// </param>
public sealed record PostConversationMessageRequest(
    string? Message,
    bool Wake = true,
    string? Sender = null,
    InboundDeliveryMode Delivery = InboundDeliveryMode.Auto);

/// <summary>
/// <c>202 Accepted</c> body for a posted conversation message: the identifiers a caller needs to read
/// the result back through the existing conversation-history routes.
/// </summary>
/// <param name="ConversationId">The conversation the message was delivered into.</param>
/// <param name="SessionId">The conversation's bound session the message landed on - never a freshly minted one.</param>
/// <param name="Wake">Echoes which mode ran, so a caller that relied on the default can confirm it.</param>
public sealed record PostConversationMessageResponse(
    string ConversationId,
    string SessionId,
    bool Wake);
