using BotNexus.Domain.Conversations;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default implementation for synchronous peer agent conversations.
/// </summary>
public sealed class AgentConversationService : IAgentConversationService
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentSupervisor _supervisor;
    private readonly ISessionStore _sessionStore;
    private readonly ILogger<AgentConversationService> _logger;

    public AgentConversationService(
        IAgentRegistry registry,
        IAgentSupervisor supervisor,
        ISessionStore sessionStore,
        ILogger<AgentConversationService> logger)
    {
        _registry = registry;
        _supervisor = supervisor;
        _sessionStore = sessionStore;
        _logger = logger;
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
        if (!_registry.Contains(request.TargetId))
            throw new KeyNotFoundException($"Target agent '{request.TargetId}' is not registered.");

        if (!initiatorDescriptor.SubAgentIds.Contains(request.TargetId.Value, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Agent '{request.InitiatorId}' is not allowed to converse with '{request.TargetId}'.");

        var sessionId = SessionId.ForAgentConversation(request.InitiatorId, request.TargetId, Guid.NewGuid().ToString("N"));
        var session = await _sessionStore.GetOrCreateAsync(sessionId, request.InitiatorId, cancellationToken).ConfigureAwait(false);
        session.SessionType = SessionType.AgentAgent;
        session.ChannelType = null;
        session.CallerId = null;
        session.Status = SessionStatus.Active;
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

        var chain = request.CallChain.Count == 0
            ? [request.InitiatorId.Value]
            : request.CallChain.Select(id => id.Value).ToArray();
        session.Metadata["callChain"] = chain;
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

            session.Status = SessionStatus.Sealed;
            session.Metadata["conversationStatus"] = "sealed";
            await _sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent conversation failed for session '{SessionId}'.", sessionId);
            session.Status = SessionStatus.Sealed;
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
}
