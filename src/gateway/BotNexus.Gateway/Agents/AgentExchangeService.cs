using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default implementation for synchronous peer agent conversations.
/// </summary>
public sealed class AgentExchangeService : IAgentExchangeService
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentSupervisor _supervisor;
    private readonly ISessionStore _sessionStore;
    private readonly IConversationStore _conversationStore;
    private readonly IOptions<Gateway.Configuration.GatewayOptions> _options;
    private readonly ILogger<AgentExchangeService> _logger;
    private readonly IOptions<PlatformConfig> _platformConfigOptions;
    private readonly CrossWorldChannelAdapter _crossWorldChannelAdapter;
    private readonly string _sourceWorldId;

    public AgentExchangeService(
        IAgentRegistry registry,
        IAgentSupervisor supervisor,
        ISessionStore sessionStore,
        IConversationStore conversationStore,
        IOptions<Gateway.Configuration.GatewayOptions> options,
        ILogger<AgentExchangeService> logger,
        IOptions<PlatformConfig>? platformConfigOptions = null,
        CrossWorldChannelAdapter? crossWorldChannelAdapter = null)
    {
        _registry = registry;
        _supervisor = supervisor;
        _sessionStore = sessionStore;
        _conversationStore = conversationStore;
        _options = options;
        _logger = logger;
        _platformConfigOptions = platformConfigOptions ?? Options.Create(new PlatformConfig());
        _crossWorldChannelAdapter = crossWorldChannelAdapter ?? new CrossWorldChannelAdapter(
            NullLogger<CrossWorldChannelAdapter>.Instance,
            new HttpClient());
        _sourceWorldId = WorldIdentityResolver.Resolve(_platformConfigOptions.Value).Id;
    }

    /// <inheritdoc />
    public async Task<AgentExchangeResult> ConverseAsync(AgentExchangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Message))
            throw new ArgumentException("Conversation message cannot be empty.", nameof(request));
        if (request.MaxTurns <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.MaxTurns), "MaxTurns must be greater than zero.");

        var initiatorDescriptor = _registry.Get(request.InitiatorId)
            ?? throw new KeyNotFoundException($"Initiator agent '{request.InitiatorId}' is not registered.");
        var isLocalTarget = _registry.Contains(request.TargetId);
        var hasCrossWorldTarget = CrossWorldAgentReference.TryParse(request.TargetId, out var parsedCrossWorldTarget);
        if (!isLocalTarget && !hasCrossWorldTarget)
            throw new KeyNotFoundException($"Target agent '{request.TargetId}' is not registered.");
        var targetDescriptor = isLocalTarget ? _registry.Get(request.TargetId) : null;

        if (!initiatorDescriptor.SubAgentIds.Contains(request.TargetId.Value, StringComparer.OrdinalIgnoreCase)
            && !IsRoleGranted(initiatorDescriptor, targetDescriptor))
            throw new UnauthorizedAccessException(
                $"Agent '{request.InitiatorId}' is not allowed to converse with '{request.TargetId}'.");

        var normalizedChain = NormalizeChain(request.CallChain, request.InitiatorId);
        EnsureCallChainAllowed(normalizedChain, request.TargetId);

        if (!isLocalTarget && parsedCrossWorldTarget is not null)
            return await ConverseCrossWorldAsync(request, parsedCrossWorldTarget, normalizedChain, cancellationToken).ConfigureAwait(false);

        // Phase 4 / F-3: create a real Conversation via IConversationStore so the exchange is
        // discoverable by ListByConversationAsync, the portal, and any future routing/permissions
        // walks. The conversation owns the lifecycle; the session is just one bounded LLM context
        // inside it.
        var conversation = await CreateExchangeConversationAsync(
            request.InitiatorId,
            request.TargetId,
            channelType: null,
            request.Objective,
            cancellationToken).ConfigureAwait(false);

        var sessionId = SessionId.Create();
        var session = await _sessionStore.GetOrCreateAsync(sessionId, request.InitiatorId, cancellationToken).ConfigureAwait(false);

        // F-6 eager-pin pattern (PR #547): set ConversationId and save BEFORE any path that could
        // observe the child session, so it is never visible to ListByConversationAsync as an orphan.
        session.Session.ConversationId = conversation.ConversationId;
        session.SessionType = SessionType.AgentAgent;
        session.ChannelType = null;
        session.CallerId = null;
        session.Status = GatewaySessionStatus.Active;
        session.Participants.Clear();
        session.Participants.Add(new SessionParticipant
        {
            CitizenId = CitizenId.Of(request.InitiatorId),
            Role = "initiator"
        });
        session.Participants.Add(new SessionParticipant
        {
            CitizenId = CitizenId.Of(request.TargetId),
            Role = "target"
        });

        session.Metadata["callChain"] = normalizedChain
            .Select(id => id.Value)
            .Append(request.TargetId.Value)
            .ToArray();
        session.Metadata["objective"] = request.Objective;
        session.Metadata["maxTurns"] = request.MaxTurns;
        session.Metadata["conversationId"] = conversation.ConversationId.Value;

        await _sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        conversation.ActiveSessionId = sessionId;
        await _conversationStore.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);

        var transcript = new List<AgentExchangeTranscriptEntry>();
        var targetHandle = await _supervisor.GetOrCreateAsync(request.TargetId, sessionId, cancellationToken).ConfigureAwait(false);

        var message = request.Message;
        var finalResponse = string.Empty;
        var objectiveMet = false;
        try
        {
            for (var turn = 0; turn < request.MaxTurns; turn++)
            {
                AddTurn(MessageRole.User, message, transcript, session);

                var response = await targetHandle.PromptAsync(message, cancellationToken).ConfigureAwait(false);
                finalResponse = response.Content ?? string.Empty;
                AddTurn(MessageRole.Assistant, finalResponse, transcript, session);

                if (IsObjectiveMet(request.Objective, finalResponse))
                {
                    objectiveMet = true;
                    break;
                }

                if (turn == request.MaxTurns - 1)
                    break;

                message = BuildFollowUpMessage(request.Objective, finalResponse);
            }

            session.Status = GatewaySessionStatus.Sealed;
            session.Metadata["conversationStatus"] = "sealed";
            await _sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            await ClearActiveSessionAsync(conversation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent conversation failed for session '{SessionId}'.", sessionId);
            session.Status = GatewaySessionStatus.Sealed;
            session.Metadata["conversationStatus"] = "error";
            session.Metadata["error"] = ex.Message;
            await _sessionStore.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            await ClearActiveSessionAsync(conversation, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new AgentExchangeResult
        {
            SessionId = sessionId,
            ConversationId = conversation.ConversationId,
            Status = "sealed",
            Turns = transcript.Count,
            FinalResponse = finalResponse,
            Transcript = transcript,
            CompletionReason = objectiveMet ? "objectiveMet" : "maxTurnsReached"
        };
    }

    private async Task<AgentExchangeResult> ConverseCrossWorldAsync(
        AgentExchangeRequest request,
        CrossWorldAgentReference parsedTarget,
        IReadOnlyList<AgentId> normalizedChain,
        CancellationToken cancellationToken)
    {
        var resolvedTarget = ResolveTarget(parsedTarget, request.TargetId);
        var permission = ResolveOutboundPermission(resolvedTarget.WorldId, request.InitiatorId);
        if (permission is null || !permission.AllowOutbound)
            throw new UnauthorizedAccessException(
                $"Outbound cross-world communication to '{resolvedTarget.WorldId}' is not allowed.");

        var peer = ResolvePeer(resolvedTarget.WorldId)
            ?? throw new InvalidOperationException(
                $"No cross-world peer configured for world '{resolvedTarget.WorldId}'.");

        // Phase 4 / F-3 (cross-world variant): create a real Conversation on the sender side too.
        // The receiver (CrossWorldFederationController) will receive the ConversationId via metadata
        // and create/reuse its own Conversation row with the same id (Phase 4 item 1b — separate PR).
        var conversation = await CreateExchangeConversationAsync(
            request.InitiatorId,
            resolvedTarget.AgentId,
            channelType: ChannelKey.From("cross-world"),
            request.Objective,
            cancellationToken).ConfigureAwait(false);

        var sessionId = SessionId.Create();
        var session = await _sessionStore.GetOrCreateAsync(sessionId, request.InitiatorId, cancellationToken).ConfigureAwait(false);
        session.Session.ConversationId = conversation.ConversationId;
        session.SessionType = SessionType.AgentAgent;
        session.ChannelType = ChannelKey.From("cross-world");
        session.CallerId = null;
        session.Status = GatewaySessionStatus.Active;
        session.Participants.Clear();
        session.Participants.Add(new SessionParticipant
        {
            CitizenId = CitizenId.Of(request.InitiatorId),
            Role = "initiator"
        });
        session.Participants.Add(new SessionParticipant
        {
            CitizenId = CitizenId.Of(resolvedTarget.AgentId),
            Role = "target"
        });

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

        var transcript = new List<AgentExchangeTranscriptEntry>();
        var message = request.Message;
        var finalResponse = string.Empty;
        var objectiveMet = false;
        string? remoteSessionId = null;

        try
        {
            for (var turn = 0; turn < request.MaxTurns; turn++)
            {
                AddTurn(MessageRole.User, message, transcript, session);

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
                            ["remoteSessionId"] = remoteSessionId
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                finalResponse = relayResponse.Response ?? string.Empty;
                remoteSessionId = relayResponse.SessionId;
                AddTurn(MessageRole.Assistant, finalResponse, transcript, session);

                if (IsObjectiveMet(request.Objective, finalResponse))
                {
                    objectiveMet = true;
                    break;
                }

                if (turn == request.MaxTurns - 1)
                    break;

                message = BuildFollowUpMessage(request.Objective, finalResponse);
            }

            session.Status = GatewaySessionStatus.Sealed;
            session.Metadata["conversationStatus"] = "sealed";
            session.Metadata["remoteSessionId"] = remoteSessionId;
            await _sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            await ClearActiveSessionAsync(conversation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cross-world conversation failed for session '{SessionId}'.", sessionId);
            session.Status = GatewaySessionStatus.Sealed;
            session.Metadata["conversationStatus"] = "error";
            session.Metadata["error"] = ex.Message;
            await _sessionStore.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            await ClearActiveSessionAsync(conversation, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new AgentExchangeResult
        {
            SessionId = sessionId,
            ConversationId = conversation.ConversationId,
            Status = "sealed",
            Turns = transcript.Count,
            FinalResponse = finalResponse,
            Transcript = transcript,
            CompletionReason = objectiveMet ? "objectiveMet" : "maxTurnsReached"
        };
    }

    /// <summary>
    /// Creates and persists a fresh <see cref="ConversationKind.AgentAgent"/> conversation for
    /// this exchange. Each <c>ConverseAsync</c> call is a bounded one-shot loop and gets its
    /// own conversation — they are never reused across calls.
    /// </summary>
    private async Task<Conversation> CreateExchangeConversationAsync(
        AgentId initiatorId,
        AgentId targetId,
        ChannelKey? channelType,
        string? objective,
        CancellationToken cancellationToken)
    {
        var conversation = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = initiatorId,
            Kind = ConversationKind.AgentAgent,
            Initiator = CitizenId.Of(initiatorId),
            Title = $"{initiatorId.Value} \u2194 {targetId.Value}",
            Purpose = string.IsNullOrWhiteSpace(objective) ? null : objective,
            Status = ConversationStatus.Active
        };

        if (channelType is { } ct)
        {
            conversation.Metadata["channelType"] = ct.Value;
        }

        return await _conversationStore.CreateAsync(conversation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears <see cref="Conversation.ActiveSessionId"/> after the exchange loop terminates so the
    /// conversation is no longer reported as "in flight". The conversation itself stays
    /// <see cref="ConversationStatus.Active"/> so it remains visible to the portal/list APIs —
    /// archiving is a separate operator-driven action.
    /// </summary>
    private async Task ClearActiveSessionAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        try
        {
            var latest = await _conversationStore.GetAsync(conversation.ConversationId, cancellationToken).ConfigureAwait(false);
            if (latest is null)
                return;
            latest.ActiveSessionId = null;
            latest.UpdatedAt = DateTimeOffset.UtcNow;
            await _conversationStore.SaveAsync(latest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ActiveSessionId is a derived diagnostic; failing to clear it must not propagate as a
            // ConverseAsync failure — the conversation is still queryable through the session store.
            _logger.LogWarning(ex,
                "Failed to clear ActiveSessionId on conversation '{ConversationId}' after exchange.",
                conversation.ConversationId);
        }
    }

    private static void AddTurn(
        MessageRole role,
        string content,
        List<AgentExchangeTranscriptEntry> transcript,
        GatewaySession session)
    {
        transcript.Add(new AgentExchangeTranscriptEntry(role.Value, content));
        session.AddEntry(new SessionEntry
        {
            Role = role,
            Content = content
        });
    }

    private static bool IsObjectiveMet(string? objective, string response)
    {
        if (string.IsNullOrWhiteSpace(objective))
            return true;

        // Only explicit completion signals; broad words like "done" cause false positives (issue #379).
        return response.Contains("OBJECTIVE MET", StringComparison.OrdinalIgnoreCase)
               || response.Contains("completed objective", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFollowUpMessage(string? objective, string latestResponse)
    {
        var targetObjective = string.IsNullOrWhiteSpace(objective)
            ? "Continue and provide your final response."
            : $"Continue working toward objective: {objective}";

        return $"{targetObjective}\n\nLatest response:\n{latestResponse}\n\n" +
               "When complete, include the phrase \"OBJECTIVE MET\" in your response.";
    }

    private static IReadOnlyList<AgentId> NormalizeChain(IReadOnlyList<AgentId> chain, AgentId initiatorId)
    {
        if (chain.Count == 0)
            return [initiatorId];
        if (string.Equals(chain[^1].Value, initiatorId.Value, StringComparison.OrdinalIgnoreCase))
            return chain;
        return [.. chain, initiatorId];
    }

    private void EnsureCallChainAllowed(IReadOnlyList<AgentId> chain, AgentId targetId)
    {
        if (chain.Any(id => string.Equals(id.Value, targetId.Value, StringComparison.OrdinalIgnoreCase)))
        {
            var chainText = string.Join(" -> ", chain.Select(id => id.Value).Append(targetId.Value));
            throw new InvalidOperationException($"Cycle detected: {chainText}");
        }

        var maxDepth = _options.Value.AgentConversationMaxDepth <= 0
            ? 1
            : _options.Value.AgentConversationMaxDepth;
        var nextDepth = chain.Count + 1;
        if (nextDepth > maxDepth)
        {
            var chainText = string.Join(" -> ", chain.Select(id => id.Value).Append(targetId.Value));
            throw new InvalidOperationException(
                $"Agent conversation call chain depth {nextDepth} exceeded maximum configured depth {maxDepth}. Chain: {chainText}");
        }
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

    /// <summary>
    /// Returns true when the initiator's <c>SubAgentRoles</c> list contains at least one role
    /// that matches the target's <c>metadata.role</c> value.
    /// </summary>
    private static bool IsRoleGranted(AgentDescriptor initiator, AgentDescriptor? target)
    {
        if (initiator.SubAgentRoles.Count == 0 || target is null)
            return false;

        if (!target.Metadata.TryGetValue("role", out var roleRaw) || roleRaw is null)
            return false;

        var targetRole = roleRaw is System.Text.Json.JsonElement je
            ? je.GetString()
            : roleRaw.ToString();

        return !string.IsNullOrWhiteSpace(targetRole)
            && initiator.SubAgentRoles.Contains(targetRole, StringComparer.OrdinalIgnoreCase);
    }
}
