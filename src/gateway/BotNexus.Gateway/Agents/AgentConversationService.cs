using BotNexus.Domain.Conversations;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
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
public sealed class AgentConversationService : IAgentConversationService
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentSupervisor _supervisor;
    private readonly ISessionStore _sessionStore;
    private readonly IOptions<Gateway.Configuration.GatewayOptions> _options;
    private readonly ILogger<AgentConversationService> _logger;
    private readonly PlatformConfig _platformConfig;
    private readonly CrossWorldChannelAdapter _crossWorldChannelAdapter;
    private readonly string _sourceWorldId;

    public AgentConversationService(
        IAgentRegistry registry,
        IAgentSupervisor supervisor,
        ISessionStore sessionStore,
        IOptions<Gateway.Configuration.GatewayOptions> options,
        ILogger<AgentConversationService> logger,
        PlatformConfig? platformConfig = null,
        CrossWorldChannelAdapter? crossWorldChannelAdapter = null)
    {
        _registry = registry;
        _supervisor = supervisor;
        _sessionStore = sessionStore;
        _options = options;
        _logger = logger;
        _platformConfig = platformConfig ?? new PlatformConfig();
        _crossWorldChannelAdapter = crossWorldChannelAdapter ?? new CrossWorldChannelAdapter(
            NullLogger<CrossWorldChannelAdapter>.Instance,
            new HttpClient());
        _sourceWorldId = WorldIdentityResolver.Resolve(_platformConfig).Id;
    }

    /// <inheritdoc />
    public async Task<AgentConversationResult> ConverseAsync(ConversationRequest request, CancellationToken cancellationToken = default)
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

        if (!initiatorDescriptor.SubAgentIds.Contains(request.TargetId.Value, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Agent '{request.InitiatorId}' is not allowed to converse with '{request.TargetId}'.");

        var normalizedChain = NormalizeChain(request.CallChain, request.InitiatorId);
        EnsureCallChainAllowed(normalizedChain, request.TargetId);

        if (!isLocalTarget && parsedCrossWorldTarget is not null)
            return await ConverseCrossWorldAsync(request, parsedCrossWorldTarget, normalizedChain, cancellationToken).ConfigureAwait(false);

        var sessionId = SessionId.ForAgentConversation(request.InitiatorId, request.TargetId, Guid.NewGuid().ToString("N"));
        var session = await _sessionStore.GetOrCreateAsync(sessionId, request.InitiatorId, cancellationToken).ConfigureAwait(false);
        session.SessionType = SessionType.AgentAgent;
        session.ChannelType = null;
        session.CallerId = null;
        session.Status = GatewaySessionStatus.Active;
        session.Participants.Clear();
        session.Participants.Add(new SessionParticipant
        {
            Type = ParticipantType.Agent,
            Id = request.InitiatorId.Value,
            Role = "initiator"
        });
        session.Participants.Add(new SessionParticipant
        {
            Type = ParticipantType.Agent,
            Id = request.TargetId.Value,
            Role = "target"
        });

        session.Metadata["callChain"] = normalizedChain
            .Select(id => id.Value)
            .Append(request.TargetId.Value)
            .ToArray();
        session.Metadata["objective"] = request.Objective;
        session.Metadata["maxTurns"] = request.MaxTurns;

        var transcript = new List<AgentConversationTranscriptEntry>();
        var targetHandle = await _supervisor.GetOrCreateAsync(request.TargetId, sessionId, cancellationToken).ConfigureAwait(false);

        var message = request.Message;
        var finalResponse = string.Empty;
        try
        {
            for (var turn = 0; turn < request.MaxTurns; turn++)
            {
                AddTurn(MessageRole.User, message, transcript, session);

                var response = await targetHandle.PromptAsync(message, cancellationToken).ConfigureAwait(false);
                finalResponse = response.Content ?? string.Empty;
                AddTurn(MessageRole.Assistant, finalResponse, transcript, session);

                if (IsObjectiveMet(request.Objective, finalResponse))
                    break;

                if (turn == request.MaxTurns - 1)
                    break;

                message = BuildFollowUpMessage(request.Objective, finalResponse);
            }

            session.Status = GatewaySessionStatus.Sealed;
            session.Metadata["conversationStatus"] = "sealed";
            await _sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent conversation failed for session '{SessionId}'.", sessionId);
            session.Status = GatewaySessionStatus.Sealed;
            session.Metadata["conversationStatus"] = "error";
            session.Metadata["error"] = ex.Message;
            await _sessionStore.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new AgentConversationResult
        {
            SessionId = sessionId,
            Status = "sealed",
            Turns = transcript.Count,
            FinalResponse = finalResponse,
            Transcript = transcript
        };
    }

    private async Task<AgentConversationResult> ConverseCrossWorldAsync(
        ConversationRequest request,
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

        var conversationId = $"{request.InitiatorId.Value}::cross::{resolvedTarget.WorldId}:{resolvedTarget.AgentId.Value}::{Guid.NewGuid():N}";
        var sessionId = SessionId.ForAgentConversation(request.InitiatorId, request.TargetId, Guid.NewGuid().ToString("N"));
        var session = await _sessionStore.GetOrCreateAsync(sessionId, request.InitiatorId, cancellationToken).ConfigureAwait(false);
        session.SessionType = SessionType.AgentAgent;
        session.ChannelType = ChannelKey.From("cross-world");
        session.CallerId = null;
        session.Status = GatewaySessionStatus.Active;
        session.Participants.Clear();
        session.Participants.Add(new SessionParticipant
        {
            Type = ParticipantType.Agent,
            Id = request.InitiatorId.Value,
            WorldId = _sourceWorldId,
            Role = "initiator"
        });
        session.Participants.Add(new SessionParticipant
        {
            Type = ParticipantType.Agent,
            Id = resolvedTarget.AgentId.Value,
            WorldId = resolvedTarget.WorldId,
            Role = "target"
        });

        session.Metadata["callChain"] = normalizedChain
            .Select(id => id.Value)
            .Append(request.TargetId.Value)
            .ToArray();
        session.Metadata["objective"] = request.Objective;
        session.Metadata["maxTurns"] = request.MaxTurns;
        session.Metadata["targetWorldId"] = resolvedTarget.WorldId;
        session.Metadata["conversationId"] = conversationId;

        var transcript = new List<AgentConversationTranscriptEntry>();
        var message = request.Message;
        var finalResponse = string.Empty;
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
                        ConversationId = conversationId,
                        Content = message,
                        SessionId = sessionId.Value,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["endpoint"] = peer.Endpoint,
                            ["apiKey"] = peer.ApiKey,
                            ["sourceWorldId"] = _sourceWorldId,
                            ["sourceAgentId"] = request.InitiatorId.Value,
                            ["targetAgentId"] = resolvedTarget.AgentId.Value,
                            ["conversationId"] = conversationId,
                            ["sourceSessionId"] = sessionId.Value,
                            ["remoteSessionId"] = remoteSessionId
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                finalResponse = relayResponse.Response ?? string.Empty;
                remoteSessionId = relayResponse.SessionId;
                AddTurn(MessageRole.Assistant, finalResponse, transcript, session);

                if (IsObjectiveMet(request.Objective, finalResponse))
                    break;

                if (turn == request.MaxTurns - 1)
                    break;

                message = BuildFollowUpMessage(request.Objective, finalResponse);
            }

            session.Status = GatewaySessionStatus.Sealed;
            session.Metadata["conversationStatus"] = "sealed";
            session.Metadata["remoteSessionId"] = remoteSessionId;
            await _sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cross-world conversation failed for session '{SessionId}'.", sessionId);
            session.Status = GatewaySessionStatus.Sealed;
            session.Metadata["conversationStatus"] = "error";
            session.Metadata["error"] = ex.Message;
            await _sessionStore.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new AgentConversationResult
        {
            SessionId = sessionId,
            Status = "sealed",
            Turns = transcript.Count,
            FinalResponse = finalResponse,
            Transcript = transcript
        };
    }

    private static void AddTurn(
        MessageRole role,
        string content,
        List<AgentConversationTranscriptEntry> transcript,
        GatewaySession session)
    {
        transcript.Add(new AgentConversationTranscriptEntry(role.Value, content));
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

        return response.Contains("objective met", StringComparison.OrdinalIgnoreCase)
               || response.Contains("completed objective", StringComparison.OrdinalIgnoreCase)
               || response.Contains("done", StringComparison.OrdinalIgnoreCase);
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
        var permission = _platformConfig.Gateway?.CrossWorldPermissions?
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
        var peers = _platformConfig.Gateway?.CrossWorld?.Peers;
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
        var explicitTargets = _platformConfig.Gateway?.CrossWorld?.Agents;
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
