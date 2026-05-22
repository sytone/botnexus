using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Invites another registered agent into the current session as a participant.
/// Upgrades the session type to MultiAgent, adds the target agent to the participants list,
/// and sends a briefing message to orient the invited agent.
/// </summary>
public sealed class InviteAgentTool(
    IAgentRegistry agentRegistry,
    IAgentExchangeService exchangeService,
    ISessionStore sessionStore,
    AgentId initiatorAgentId,
    SessionId sessionId) : IAgentTool
{
    private const int MaxMultiAgentDepth = 5;

    public string Name => "invite_agent";
    public string Label => "Invite Agent";

    public Tool Definition => new(
        Name,
        "Invite another registered agent into the current session as a participant. The invited agent receives a briefing context and can then converse in the shared session.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "agentId": { "type": "string", "description": "ID of the agent to invite." },
                "role": { "type": "string", "description": "Role the agent will play in the session (e.g., 'reviewer', 'specialist', 'assistant')." },
                "context": { "type": "string", "description": "Briefing context to send to the invited agent." }
              },
              "required": ["agentId", "context"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(ReadString(arguments, "agentId")))
            throw new ArgumentException("Missing required argument: agentId.");
        if (string.IsNullOrWhiteSpace(ReadString(arguments, "context")))
            throw new ArgumentException("Missing required argument: context.");
        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var targetAgentIdStr = ReadString(arguments, "agentId")
            ?? throw new ArgumentException("Missing required argument: agentId.");
        var context = ReadString(arguments, "context")
            ?? throw new ArgumentException("Missing required argument: context.");
        var role = ReadString(arguments, "role");

        var targetAgentId = AgentId.From(targetAgentIdStr);

        // Verify target agent exists
        var targetDescriptor = agentRegistry.Get(targetAgentId);
        if (targetDescriptor is null)
        {
            return ErrorResult($"Agent '{targetAgentIdStr}' not found in registry.");
        }

        // Load current session for cycle detection and participant count
        var currentSession = await sessionStore.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (currentSession is null)
        {
            return ErrorResult("Current session not found.");
        }

        // Cycle detection: prevent an agent from inviting itself or re-inviting already present agents
        if (targetAgentId.Equals(initiatorAgentId))
        {
            return ErrorResult("An agent cannot invite itself.");
        }

        var existingParticipant = currentSession.Participants
            .FirstOrDefault(p => p.Type == ParticipantType.Agent
                && string.Equals(p.Id, targetAgentIdStr, StringComparison.OrdinalIgnoreCase));
        if (existingParticipant is not null)
        {
            return ErrorResult($"Agent '{targetAgentIdStr}' is already a participant in this session.");
        }

        // Depth limit: prevent runaway invitation chains
        var agentParticipantCount = currentSession.Participants.Count(p => p.Type == ParticipantType.Agent);
        if (agentParticipantCount >= MaxMultiAgentDepth)
        {
            return ErrorResult($"Maximum multi-agent depth ({MaxMultiAgentDepth}) reached for this session.");
        }

        // Add target agent as participant
        var participant = new SessionParticipant
        {
            Type = ParticipantType.Agent,
            Id = targetAgentIdStr,
            Role = role
        };
        currentSession.Participants.Add(participant);

        // Upgrade session type to MultiAgent
        currentSession.SessionType = SessionType.MultiAgent;

        // Persist the updated session
        await sessionStore.SaveAsync(currentSession, cancellationToken).ConfigureAwait(false);

        // Send briefing message to the invited agent via agent exchange
        var briefing = $"You have been invited to join a collaborative session by agent '{initiatorAgentId.Value}'." +
                       (role is not null ? $" Your role: {role}." : string.Empty) +
                       $"\n\nContext:\n{context}";

        var exchangeResult = await exchangeService.ConverseAsync(
            new AgentExchangeRequest
            {
                InitiatorId = initiatorAgentId,
                TargetId = targetAgentId,
                Message = briefing,
                Objective = $"Join collaborative session as {role ?? "participant"} and acknowledge readiness.",
                MaxTurns = 1,
                CallChain = [initiatorAgentId]
            },
            cancellationToken).ConfigureAwait(false);

        var result = new
        {
            agentId = targetAgentIdStr,
            role,
            status = "invited",
            sessionType = SessionType.MultiAgent.Value,
            participantCount = currentSession.Participants.Count(p => p.Type == ParticipantType.Agent),
            briefingResponse = exchangeResult.FinalResponse
        };

        return new AgentToolResult(
            [new AgentToolContent(AgentToolContentType.Text, JsonSerializer.Serialize(result, JsonOptions))]);
    }

    private static AgentToolResult ErrorResult(string message) =>
        new([new AgentToolContent(AgentToolContentType.Text,
            JsonSerializer.Serialize(new { error = message }, JsonOptions))]);

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
