using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default implementation for synchronous peer agent conversations between two locally-registered
/// agents (in-world peer exchange).
/// </summary>
/// <remarks>
/// <para>
/// Cross-world federation routing was split out into <see cref="ICrossWorldExchangeRouter"/> and
/// the shared turn loop into <see cref="AgentExchangeTurnEngine"/> as part of #1542 (SRP). This
/// service now owns only the in-world concerns: registration/role-grant authorization, call-chain
/// cycle/depth enforcement, budget admission, and the local target handle + completion gate. When
/// the target parses as a cross-world reference it delegates to the router; everything else is the
/// local turn loop driven by the engine.
/// </para>
/// <para>
/// The constructor still accepts the optional federation parameters
/// (<c>platformConfigOptions</c>, <c>crossWorldChannelAdapter</c>) for backward compatibility:
/// when no <see cref="ICrossWorldExchangeRouter"/> is injected, it composes a default engine +
/// router from them. In production DI the engine and router are registered and injected directly,
/// so this service no longer references <c>CrossWorldChannelAdapter</c> or the source world id in
/// its own logic.
/// </para>
/// </remarks>
public sealed class AgentExchangeService : IAgentExchangeService
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentSupervisor _supervisor;
    private readonly ISessionStore _sessionStore;
    private readonly IConversationStore _conversationStore;
    private readonly IOptions<Gateway.Configuration.GatewayOptions> _options;
    private readonly ILogger<AgentExchangeService> _logger;
    private readonly IOptions<AgentExchangeOptions> _exchangeOptions;
    private readonly AgentExchangeBudgetTracker? _budgetTracker;
    private readonly AgentExchangeTurnEngine _turnEngine;
    private readonly ICrossWorldExchangeRouter _crossWorldRouter;

    public AgentExchangeService(
        IAgentRegistry registry,
        IAgentSupervisor supervisor,
        ISessionStore sessionStore,
        IConversationStore conversationStore,
        IOptions<Gateway.Configuration.GatewayOptions> options,
        ILogger<AgentExchangeService> logger,
        IOptions<PlatformConfig>? platformConfigOptions = null,
        CrossWorldChannelAdapter? crossWorldChannelAdapter = null,
        IOptions<AgentExchangeOptions>? exchangeOptions = null,
        AgentExchangeBudgetTracker? budgetTracker = null,
        AgentExchangeTurnEngine? turnEngine = null,
        ICrossWorldExchangeRouter? crossWorldRouter = null)
    {
        _registry = registry;
        _supervisor = supervisor;
        _sessionStore = sessionStore;
        _conversationStore = conversationStore;
        _options = options;
        _logger = logger;
        _exchangeOptions = exchangeOptions ?? Options.Create(new AgentExchangeOptions());
        _budgetTracker = budgetTracker;

        // The turn engine single-sources the shared loop/seal/archive; the router owns cross-world
        // federation. Both are injected in production DI. When omitted (the local-only construction
        // path used by unit tests and the cross-world tests that pass platformConfig + adapter), we
        // compose defaults so behaviour is identical to the pre-#1542 single-class service.
        _turnEngine = turnEngine ?? new AgentExchangeTurnEngine(
            sessionStore,
            conversationStore,
            logger,
            budgetTracker);

        _crossWorldRouter = crossWorldRouter ?? new CrossWorldExchangeRouter(
            _turnEngine,
            sessionStore,
            conversationStore,
            platformConfigOptions ?? Options.Create(new PlatformConfig()),
            crossWorldChannelAdapter ?? new CrossWorldChannelAdapter(
                NullLogger<CrossWorldChannelAdapter>.Instance,
                new HttpClient()));
    }

    /// <inheritdoc />
    public async Task<AgentExchangeResult> ConverseAsync(AgentExchangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Message))
            throw new ArgumentException("Conversation message cannot be empty.", nameof(request));
        if (request.MaxTurns <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.MaxTurns), "MaxTurns must be greater than zero.");

        // #2136: sub-agent worker archetypes (researcher, coder, planner, reviewer, writer, analyst)
        // are implementation-only roles, not conversational peers. Reject them deterministically here
        // - BEFORE any session or conversation is created - so a stale conversation/session targeting
        // an archetype id fails fast with actionable guidance instead of reaching descriptor creation,
        // hitting "ModelId is required; ApiProvider is required", and surfacing as a fatal
        // UnobservedTaskException breadcrumb.
        if (BuiltInArchetypes.IsReserved(request.TargetId.Value))
            throw new ArgumentException(
                $"'{request.TargetId.Value}' is a built-in sub-agent archetype, not a conversational agent. "
                + $"Use spawn_subagent(archetype: \"{request.TargetId.Value}\") to delegate work to it, or "
                + "agent_converse only with a genuine registered named agent (see list_agents).",
                nameof(request));

        var initiatorDescriptor = _registry.Get(request.InitiatorId)
            ?? throw new KeyNotFoundException($"Initiator agent '{request.InitiatorId}' is not registered.");
        var isLocalTarget = _registry.Contains(request.TargetId);
        var hasCrossWorldTarget = CrossWorldAgentReference.TryParse(request.TargetId, out var parsedCrossWorldTarget);
        if (!isLocalTarget && !hasCrossWorldTarget)
            throw new KeyNotFoundException($"Target agent '{request.TargetId}' is not registered.");
        var targetDescriptor = isLocalTarget ? _registry.Get(request.TargetId) : null;

        if (!_exchangeOptions.Value.IsOpen)
        {
            if (!initiatorDescriptor.SubAgentIds.Contains(request.TargetId.Value, StringComparer.OrdinalIgnoreCase)
                && !IsRoleGranted(initiatorDescriptor, targetDescriptor))
                throw new UnauthorizedAccessException(
                    $"Agent '{request.InitiatorId}' is not allowed to converse with '{request.TargetId}'.");
        }

        _logger.LogInformation(
            "Agent exchange initiated: {Initiator} -> {Target} (policy={Policy})",
            request.InitiatorId.Value, request.TargetId.Value, _exchangeOptions.Value.AccessPolicy);

        var normalizedChain = NormalizeChain(request.CallChain, request.InitiatorId);
        EnsureCallChainAllowed(normalizedChain, request.TargetId);

        // Budget enforcement: daily cap, loop detection, cooldown
        _budgetTracker?.EnsureWithinBudget(request.InitiatorId, request.TargetId);

        if (!isLocalTarget && parsedCrossWorldTarget is not null)
            return await _crossWorldRouter.ConverseCrossWorldAsync(request, parsedCrossWorldTarget, normalizedChain, cancellationToken).ConfigureAwait(false);

        // Phase 4 / F-3: create a real Conversation via IConversationStore so the exchange is
        // discoverable by ListByConversationAsync, the portal, and any future routing/permissions
        // walks. The conversation owns the lifecycle; the session is just one bounded LLM context
        // inside it.
        var conversation = await _turnEngine.CreateExchangeConversationAsync(
            request.InitiatorId,
            request.TargetId,
            channelType: null,
            request.Objective,
            cancellationToken).ConfigureAwait(false);

        var sessionId = SessionId.Create();
        var session = await _sessionStore.GetOrCreateAsync(sessionId, request.InitiatorId, cancellationToken).ConfigureAwait(false);

        // F-6 eager-pin pattern (PR #547): set ConversationId and save BEFORE any path that could
        // observe the child session, so it is never visible to ListByConversationAsync as an orphan.
        session.ConversationId = conversation.ConversationId;
        session.SessionType = SessionType.AgentAgent;
        session.ChannelType = null;
        session.CallerId = null;
        session.Status = GatewaySessionStatus.Active;

        // P9-F: Participants live on the Conversation, not the Session. The agent-exchange
        // handshake pre-registers the initiator + target so any later participant-based
        // query (e.g. portal's responder-side inbox via IConversationStore.ListForCitizenAsync)
        // resolves the conversation for both citizens.
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
                    CitizenId = CitizenId.Of(request.TargetId),
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
        session.Metadata["conversationId"] = conversation.ConversationId.Value;

        await _sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        conversation.ActiveSessionId = sessionId;
        await _conversationStore.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);

        // F-11 local turn: the completion gate is pinned per-turn (a fresh active-exchange id is
        // saved BEFORE the prompt and the prior finish payload cleared), then consumed from the
        // reloaded session after the prompt so a stale finishedAgentExchangeId can never replay.
        // The target handle is created lazily on the first turn so a creation failure is caught by
        // the shared loop's error arm and seals the session (the original behaviour).
        IAgentHandle? targetHandle = null;
        return await _turnEngine.RunExchangeLoopAsync(
            request,
            conversation,
            sessionId,
            session,
            sendTurnAsync: async (turn, message, ct) =>
            {
                targetHandle ??= await _supervisor.GetOrCreateAsync(request.TargetId, sessionId, ct).ConfigureAwait(false);

                var exchangeId = Guid.NewGuid().ToString("N");
                AgentExchangeCompletionGate.PrepareTurn(session.Metadata, exchangeId);
                await _sessionStore.SaveAsync(session, ct).ConfigureAwait(false);

                var response = await targetHandle.PromptAsync(message, ct).ConfigureAwait(false);
                var responseText = response.Content ?? string.Empty;

                // Reload the session: the tool execution mutated Session.Metadata in the store via
                // its own ISessionStore handle, so the in-memory copy here may be stale.
                var refreshed = await _sessionStore.GetAsync(sessionId, ct).ConfigureAwait(false)
                    ?? session;

                if (AgentExchangeCompletionGate.TryConsume(response, refreshed.Metadata, exchangeId, out var finishReason, out var finishSummary))
                {
                    // Mirror the consumed payload back onto the working session so the post-turn
                    // SaveAsync persists the canonical metadata view.
                    if (!ReferenceEquals(refreshed, session))
                    {
                        session.ExchangeCompletion = (session.ExchangeCompletion ?? new AgentExchangeCompletionState()) with
                        {
                            FinishedExchangeId = exchangeId,
                            FinishedReason = finishReason ?? string.Empty,
                            FinishedSummary = string.IsNullOrEmpty(finishSummary) ? null : finishSummary
                        };
                    }
                    return new AgentExchangeTurnEngine.ExchangeTurnOutcome(responseText, Finished: true, finishReason, finishSummary);
                }

                return new AgentExchangeTurnEngine.ExchangeTurnOutcome(responseText, Finished: false, null, null);
            },
            beforeSeal: s => s.ExchangeCompletion = s.ExchangeCompletion is { } c
                ? c with { ActiveExchangeId = null }
                : null,
            onSealSuccess: static _ => { },
            cancellationToken).ConfigureAwait(false);
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
