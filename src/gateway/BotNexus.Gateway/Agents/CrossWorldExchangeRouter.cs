using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default <see cref="ICrossWorldExchangeRouter"/>: resolves the remote world/peer, enforces
/// outbound permission, and relays each exchange turn over <see cref="CrossWorldChannelAdapter"/>.
/// The shared turn loop / session lifecycle is delegated to <see cref="AgentExchangeTurnEngine"/>.
/// </summary>
/// <remarks>
/// Extracted from <see cref="AgentExchangeService"/> (#1542). All federation plumbing — peer/
/// permission/target resolution, the source world id, and the cross-world adapter — lives here so
/// the in-world service no longer carries it. The cross-world send/relay code below is the original
/// <c>ConverseCrossWorldAsync</c> body, moved verbatim and re-pointed at the injected turn engine.
/// </remarks>
public sealed class CrossWorldExchangeRouter : ICrossWorldExchangeRouter
{
    private readonly AgentExchangeTurnEngine _turnEngine;
    private readonly ISessionStore _sessionStore;
    private readonly IConversationStore _conversationStore;
    private readonly IOptions<PlatformConfig> _platformConfigOptions;
    private readonly CrossWorldChannelAdapter _crossWorldChannelAdapter;
    private readonly string _sourceWorldId;

    public CrossWorldExchangeRouter(
        AgentExchangeTurnEngine turnEngine,
        ISessionStore sessionStore,
        IConversationStore conversationStore,
        IOptions<PlatformConfig> platformConfigOptions,
        CrossWorldChannelAdapter crossWorldChannelAdapter)
    {
        _turnEngine = turnEngine;
        _sessionStore = sessionStore;
        _conversationStore = conversationStore;
        _platformConfigOptions = platformConfigOptions;
        _crossWorldChannelAdapter = crossWorldChannelAdapter;
        _sourceWorldId = WorldIdentityResolver.Resolve(_platformConfigOptions.Value).Id;
    }

    /// <inheritdoc />
    public async Task<AgentExchangeResult> ConverseCrossWorldAsync(
        AgentExchangeRequest request,
        CrossWorldAgentReference parsedTarget,
        IReadOnlyList<AgentId> normalizedChain,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(parsedTarget);
        ArgumentNullException.ThrowIfNull(normalizedChain);

        var resolvedTarget = ResolveTarget(parsedTarget, request.TargetId);
        var permission = ResolveOutboundPermission(resolvedTarget.WorldId, request.InitiatorId);
        if (permission is null || !permission.AllowOutbound)
            throw new UnauthorizedAccessException(
                $"Outbound cross-world communication to '{resolvedTarget.WorldId}' is not allowed.");

        var peer = ResolvePeer(resolvedTarget.WorldId)
            ?? throw new InvalidOperationException(
                $"No cross-world peer configured for world '{resolvedTarget.WorldId}'.");

        // Phase 4 / F-3 (cross-world variant): create a real Conversation on the sender side.
        // The receiver (CrossWorldFederationController.RelayAsync) creates its OWN local Conversation
        // owned by the target agent and pins its receiver session to that conversation's id (not this
        // sender-side ConversationId). Source identity is preserved on the receiver's
        // Conversation.Metadata for audit; ConversationIds are NOT shared across worlds because
        // doing so would force two worlds' stores to agree on a global id space.
        var conversation = await _turnEngine.CreateExchangeConversationAsync(
            request.InitiatorId,
            resolvedTarget.AgentId,
            channelType: ChannelKey.From("cross-world"),
            request.Objective,
            cancellationToken).ConfigureAwait(false);

        var sessionId = SessionId.Create();
        var session = await _sessionStore.GetOrCreateAsync(sessionId, request.InitiatorId, cancellationToken).ConfigureAwait(false);
        session.ConversationId = conversation.ConversationId;
        session.SessionType = SessionType.AgentAgent;
        session.ChannelType = ChannelKey.From("cross-world");
        session.CallerId = null;
        session.Status = GatewaySessionStatus.Active;

        // P9-F: Participants live on the Conversation (cross-world variant). The remote
        // target is identified by its in-world AgentId so the local
        // IConversationStore.ListForCitizenAsync lookup works without cross-world plumbing.
        await _conversationStore.AddParticipantsAsync(
            conversation.ConversationId,
            [
                new SessionParticipant
                {
                    CitizenId = CitizenId.Of(request.InitiatorId),
                    Role = "initiator"
                },
                new SessionParticipant
                {
                    CitizenId = CitizenId.Of(resolvedTarget.AgentId),
                    Role = "target"
                }
            ],
            cancellationToken).ConfigureAwait(false);

        session.Metadata["callChain"] = normalizedChain
            .Select(id => id.Value)
            .Append(request.TargetId.Value)
            .ToArray();
        session.Metadata["objective"] = request.Objective;
        session.Metadata["maxTurns"] = request.MaxTurns;
        session.Metadata["sourceWorldId"] = _sourceWorldId;
        session.Metadata["targetWorldId"] = resolvedTarget.WorldId;
        session.Metadata["conversationId"] = conversation.ConversationId.Value;

        await _sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        conversation.ActiveSessionId = sessionId;
        await _conversationStore.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);

        // Cross-world turn: the remote receiver owns finish detection (the target agent runs in
        // the remote process), so completion arrives as a CrossWorldRelayResponse flag rather than
        // via the local completion gate. remoteSessionId is threaded across turns (captured below)
        // so retries reuse the receiver's session, and stamped on the session at seal.
        string? remoteSessionId = null;
        return await _turnEngine.RunExchangeLoopAsync(
            request,
            conversation,
            sessionId,
            session,
            sendTurnAsync: async (turn, message, ct) =>
            {
                // P9-C: tell the receiver this is the final relay turn so it archives its local
                // conversation. Without this signal the receiver only archives when the target
                // agent invokes finish_agent_exchange, which leaves the receiver-side conversation
                // Active forever for single-shot (no objective) and max-turns-reached exchanges.
                var isFinalTurn = string.IsNullOrWhiteSpace(request.Objective)
                                  || turn == request.MaxTurns - 1;

                // Per-turn idempotency key: the receiver uses this to skip re-appending the user
                // turn if the same key is already the last history entry. Prevents duplicate user
                // turns when the sender cancels mid-turn and retries with the same RemoteSessionId.
                var turnId = Guid.NewGuid().ToString("N");

                var relayResponse = await _crossWorldChannelAdapter.ExchangeAsync(
                    new OutboundMessage
                    {
                        ChannelType = ChannelKey.From("cross-world"),
                        ChannelAddress = ChannelAddress.From(peer.Endpoint ?? string.Empty), // target endpoint is the channel address
                        Content = message,
                        SessionId = sessionId.Value,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["endpoint"] = peer.Endpoint,
                            ["apiKey"] = peer.ApiKey,
                            ["sourceWorldId"] = _sourceWorldId,
                            ["sourceAgentId"] = request.InitiatorId.Value,
                            ["targetAgentId"] = resolvedTarget.AgentId.Value,
                            ["conversationId"] = conversation.ConversationId.Value,
                            ["sourceSessionId"] = sessionId.Value,
                            ["remoteSessionId"] = remoteSessionId,
                            ["closeAfterResponse"] = isFinalTurn,
                            ["turnId"] = turnId
                        }
                    },
                    ct).ConfigureAwait(false);

                var responseText = relayResponse.Response ?? string.Empty;
                remoteSessionId = relayResponse.SessionId;

                // Phase 8 (F-11) cross-world: the remote receiver propagates the finish-tool
                // decision via CrossWorldRelayResponse.ExchangeFinished/FinishReason/FinishSummary.
                return relayResponse.ExchangeFinished
                    ? new AgentExchangeTurnEngine.ExchangeTurnOutcome(responseText, Finished: true, relayResponse.FinishReason, relayResponse.FinishSummary)
                    : new AgentExchangeTurnEngine.ExchangeTurnOutcome(responseText, Finished: false, null, null);
            },
            beforeSeal: static _ => { },
            onSealSuccess: s => s.Metadata["remoteSessionId"] = remoteSessionId,
            cancellationToken).ConfigureAwait(false);
    }

    private CrossWorldPermissionConfig? ResolveOutboundPermission(string worldId, AgentId initiatorId)
    {
        var permission = _platformConfigOptions.Value.Gateway?.CrossWorldPermissions?
            .FirstOrDefault(item => string.Equals(item.TargetWorldId, worldId, StringComparison.OrdinalIgnoreCase));
        if (permission is null)
            return null;

        if (permission.AllowedAgents is not { Count: > 0 })
            return permission;

        return permission.AllowedAgents.Any(agent => string.Equals(agent, initiatorId.Value, StringComparison.OrdinalIgnoreCase))
            ? permission
            : null;
    }

    private CrossWorldPeerConfig? ResolvePeer(string worldId)
    {
        var peers = _platformConfigOptions.Value.Gateway?.CrossWorld?.Peers;
        if (peers is null || peers.Count == 0)
            return null;

        if (peers.TryGetValue(worldId, out var direct) && direct.Enabled)
            return direct;

        return peers.Values.FirstOrDefault(peer =>
            peer.Enabled &&
            !string.IsNullOrWhiteSpace(peer.WorldId) &&
            string.Equals(peer.WorldId, worldId, StringComparison.OrdinalIgnoreCase));
    }

    private CrossWorldAgentReference ResolveTarget(CrossWorldAgentReference fallback, AgentId requestedTarget)
    {
        var explicitTargets = _platformConfigOptions.Value.Gateway?.CrossWorld?.Agents;
        if (explicitTargets is null || !explicitTargets.TryGetValue(requestedTarget.Value, out var configuredTarget))
            return fallback;

        if (string.IsNullOrWhiteSpace(configuredTarget.WorldId) || string.IsNullOrWhiteSpace(configuredTarget.AgentId))
            return fallback;

        return new CrossWorldAgentReference
        {
            WorldId = configuredTarget.WorldId,
            AgentId = AgentId.From(configuredTarget.AgentId)
        };
    }
}
