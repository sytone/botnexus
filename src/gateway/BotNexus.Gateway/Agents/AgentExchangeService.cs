using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Audit;
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

    /// <summary>
    /// Per-target admission control for inbound exchanges (#3494). Optional-with-fallback so the
    /// many direct-construction test call sites keep compiling; every instance still gets a real
    /// queue, because "no queue" is the defect itself and must not be reachable by omission.
    /// </summary>
    private readonly AgentExchangeInboundQueue _inboundQueue;

    /// <summary>
    /// Publishes handoff milestones back into the initiating conversation (#3176). Optional-with-
    /// fallback so the many direct-construction test call sites keep compiling; the no-op instance
    /// makes "emit progress" unconditional at every call site instead of null-guarded.
    /// </summary>
    private readonly IAgentExchangeProgressNotifier _progress;

    /// <summary>
    /// The single execution-layer tool-audit sink (#2614 AC4). The local agent-exchange turn is a
    /// blocking <c>PromptAsync</c> boundary, so before this slice a target agent could run any
    /// number of side-effecting tools during an exchange and leave nothing but its final text
    /// behind. Optional-with-fallback so the many direct-construction test call sites keep working
    /// while production DI still supplies the one registered instance.
    /// </summary>
    private readonly IToolAuditSink _toolAudit;

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
        ICrossWorldExchangeRouter? crossWorldRouter = null,
        IToolAuditSink? toolAudit = null,
        IAgentExchangeProgressNotifier? progressNotifier = null,
        AgentExchangeInboundQueue? inboundQueue = null)
    {
        _registry = registry;
        _supervisor = supervisor;
        _sessionStore = sessionStore;
        _conversationStore = conversationStore;
        _options = options;
        _logger = logger;
        _exchangeOptions = exchangeOptions ?? Options.Create(new AgentExchangeOptions());
        _budgetTracker = budgetTracker;
        _toolAudit = toolAudit ?? DefaultToolAuditSink.Instance;
        _progress = progressNotifier ?? NullAgentExchangeProgressNotifier.Instance;
        _inboundQueue = inboundQueue ?? new AgentExchangeInboundQueue(_exchangeOptions);

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
        // #2878 target resolution, in strict precedence order:
        //   1. exact agent id  - always wins, so no existing id-addressed call can change meaning;
        //   2. cross-world reference parsing - attempted BEFORE the display-name fallback, so a
        //      "world:agent" target keeps its federation behaviour unchanged;
        //   3. unambiguous case-insensitive display name - resolved to the owning agent id.
        // Resolution happens BEFORE the access-policy check below and rewrites request.TargetId, so
        // the policy (and the call chain, budget, conversation and supervisor handle) all evaluate
        // against the RESOLVED agent. Display-name addressing therefore cannot bypass a whitelist.
        var isLocalTarget = _registry.Contains(request.TargetId);
        var hasCrossWorldTarget = CrossWorldAgentReference.TryParse(request.TargetId, out var parsedCrossWorldTarget);
        if (!isLocalTarget && !hasCrossWorldTarget)
        {
            var displayNameMatches = FindByDisplayName(request.TargetId);
            if (displayNameMatches.Count != 1)
                throw new KeyNotFoundException(BuildTargetResolutionFailureMessage(request.TargetId, displayNameMatches));

            var resolved = displayNameMatches[0];
            _logger.LogInformation(
                "Agent exchange target '{RequestedTarget}' resolved by display name to agent '{ResolvedTarget}'.",
                request.TargetId.Value, resolved.AgentId.Value);
            request = request with { TargetId = resolved.AgentId };
            isLocalTarget = true;
        }

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
        // #3176: a refusal here is a HALT the initiating conversation must be able to see - it is
        // the one terminal outcome that produces no child conversation at all, so without an event
        // the delegating thread would show a bare exception with no handoff context.
        try
        {
            _budgetTracker?.EnsureWithinBudget(request.InitiatorId, request.TargetId);
        }
        catch (InvalidOperationException ex)
        {
            await PublishProgressAsync(request, AgentExchangeProgressPhase.Halted, null, null, ex.Message, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (!isLocalTarget && parsedCrossWorldTarget is not null)
            return await _crossWorldRouter.ConverseCrossWorldAsync(request, parsedCrossWorldTarget, normalizedChain, cancellationToken).ConfigureAwait(false);

        // #3494 admission gate. A local target is an in-process agent with exactly ONE execution
        // slot, so a second exchange arriving while it is busy has to wait somewhere. Before this
        // gate it waited nowhere: it minted a conversation + session, blocked downstream, and was
        // killed by the caller's own deadline, leaving a one-row Active session and a bare
        // "task was canceled".
        //
        // The gate sits BEFORE conversation/session creation on purpose. That ordering is what
        // satisfies AC2 and AC4 at once: a refused or never-dispatched exchange mints no state at
        // all, so there is nothing for a reaper to clean up and no session to strand. It is also
        // placed AFTER the cross-world return above, because a federated target runs in another
        // process and its slot is not ours to gate.
        using var slot = await _inboundQueue.AcquireAsync(request.TargetId, cancellationToken).ConfigureAwait(false);

        // Phase 4 / F-3: create a real Conversation via IConversationStore so the exchange is
        // discoverable by ListByConversationAsync, the portal, and any future routing/permissions
        // walks. The conversation owns the lifecycle; the session is just one bounded LLM context
        // inside it.
        var conversation = await _turnEngine.CreateExchangeConversationAsync(
            request.InitiatorId,
            request.TargetId,
            channelType: null,
            request.Objective,
            cancellationToken,
            request.InitiatorConversationId).ConfigureAwait(false);

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

        // #3176 AC7: the STARTED event is emitted strictly AFTER the eager pin + save above, so the
        // event can never advertise a child conversation whose session is not yet pinned to it.
        // Deliberately placed here rather than inside the turn engine: the engine is shared with the
        // cross-world router, and federated handoff visibility is out of scope for this issue.
        await PublishProgressAsync(
            request,
            AgentExchangeProgressPhase.Started,
            conversation.ConversationId,
            sessionId,
            reason: null,
            turns: null,
            cancellationToken).ConfigureAwait(false);

        // F-11 local turn: the completion gate is pinned per-turn (a fresh active-exchange id is
        // saved BEFORE the prompt and the prior finish payload cleared), then consumed from the
        // reloaded session after the prompt so a stale finishedAgentExchangeId can never replay.
        // The target handle is created lazily on the first turn so a creation failure is caught by
        // the shared loop's error arm and seals the session (the original behaviour).
        IAgentHandle? targetHandle = null;
        AgentExchangeResult result;
        try
        {
            result = await _turnEngine.RunExchangeLoopAsync(
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
                // #2614 AC4: capture the tool timeline this turn executed and hand it to the shared
                // loop, which persists it between the user and assistant rows.
                var toolEntries = _toolAudit.ProjectBlockingRun(_toolAudit.CaptureBlockingRun(response));

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
                    return new AgentExchangeTurnEngine.ExchangeTurnOutcome(responseText, Finished: true, finishReason, finishSummary, toolEntries);
                }

                return new AgentExchangeTurnEngine.ExchangeTurnOutcome(responseText, Finished: false, null, null, toolEntries);
            },
            beforeSeal: s => s.ExchangeCompletion = s.ExchangeCompletion is { } c
                ? c with { ActiveExchangeId = null }
                : null,
            onSealSuccess: static _ => { },
            cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // #553 parity: caller cancellation is not an exchange failure and does not seal the
            // session, so it does not get a terminal event either. Rethrow untouched.
            //
            // #3494 AC4: but it must not vanish silently either. The acceptance clause offers
            // "seals OR marks", and marking is the only one compatible with #553 - sealing would
            // reintroduce the 409 that made caller retries impossible. So we stamp an outcome and
            // leave the status Active: the session stays retryable, and a reaper (or an operator
            // reading the store) can now tell an abandoned exchange from a healthy in-flight one,
            // which was impossible when both looked like "Active with one history row".
            await MarkCallerCancelledAsync(sessionId).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await PublishProgressAsync(
                request,
                AgentExchangeProgressPhase.Failed,
                conversation.ConversationId,
                sessionId,
                ex.Message,
                turns: null,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // AC4: a turn-cap exit is a HALT, not a completion. The turn engine already distinguishes
        // the two via CompletionReason; this maps that existing distinction onto the progress phase
        // so a reader of the initiating conversation can tell "the target finished" from "we ran
        // out of turns" without parsing the status text.
        var halted = string.Equals(result.CompletionReason, "maxTurnsReached", StringComparison.Ordinal);
        await PublishProgressAsync(
            request,
            halted ? AgentExchangeProgressPhase.Halted : AgentExchangeProgressPhase.Completed,
            result.ConversationId,
            result.SessionId,
            result.CompletionReason,
            result.Turns,
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Stamps <c>exchangeOutcome=callerCancelled</c> on an exchange session abandoned by a caller
    /// deadline, WITHOUT sealing it (#3494 AC4).
    /// </summary>
    /// <remarks>
    /// Always persists with <see cref="CancellationToken.None"/>: the caller's token has already
    /// fired by definition, so threading it here would make the marker the one thing cancellation
    /// reliably prevents. Failures are logged and swallowed - a diagnostic marker turning a
    /// cancellation into a different exception would be a strictly worse bug than a missing marker.
    /// </remarks>
    private async Task MarkCallerCancelledAsync(SessionId sessionId)
    {
        try
        {
            var latest = await _sessionStore.GetAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (latest is null)
                return;
            latest.Metadata["exchangeOutcome"] = "callerCancelled";
            await _sessionStore.SaveAsync(latest, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not mark exchange session '{SessionId}' as caller-cancelled.", sessionId);
        }
    }

    /// <summary>
    /// Emits one handoff milestone to the initiating conversation (#3176).
    /// </summary>
    /// <remarks>
    /// A single funnel so every emission site produces an identically-shaped event, and so the
    /// "observability must never break the exchange" rule is enforced in one place: the notifier
    /// contract forbids throwing, and this method double-guards it because a progress failure
    /// turning a successful handoff into a thrown call would be a strictly worse bug than silence.
    /// </remarks>
    private async Task PublishProgressAsync(
        AgentExchangeRequest request,
        AgentExchangeProgressPhase phase,
        ConversationId? childConversationId,
        SessionId? childSessionId,
        string? reason,
        int? turns,
        CancellationToken cancellationToken)
    {
        try
        {
            await _progress.PublishAsync(
                new AgentExchangeProgressEvent
                {
                    Phase = phase,
                    InitiatorId = request.InitiatorId,
                    TargetId = request.TargetId,
                    InitiatorSessionId = request.InitiatorSessionId,
                    InitiatorConversationId = request.InitiatorConversationId,
                    ChildConversationId = childConversationId,
                    ChildSessionId = childSessionId,
                    Reason = reason,
                    Turns = turns
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Agent exchange progress '{Phase}' could not be published for {Initiator} -> {Target}. Exchange continues.",
                phase, request.InitiatorId.Value, request.TargetId.Value);
        }
    }

    /// <summary>
    /// Builds the diagnostic thrown when an <c>agent_converse</c> target id resolves to nothing (#2877).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This throw site sits BEFORE any policy evaluation, so a bare "is not registered" was
    /// indistinguishable from an authorization denial: the caller's rational next step was to stop
    /// trying rather than to correct the identifier. The registry already holds the display name of
    /// every agent, so the information needed to redirect the caller is in hand right here.
    /// </para>
    /// <para>
    /// Since #2878 a SINGLE display-name match no longer reaches this method - it resolves and the
    /// exchange proceeds. This builder therefore handles only the two remaining failures: ambiguity
    /// (two or more agents share the display name) and no match at all. Ambiguity is reported as
    /// ambiguity rather than collapsed into an arbitrary single choice, because silently picking one
    /// of two same-named agents would route a conversation to the wrong peer.
    /// </para>
    /// </remarks>
    private static string BuildTargetResolutionFailureMessage(AgentId targetId, IReadOnlyList<AgentDescriptor> displayNameMatches)
    {
        var prefix = $"Target agent '{targetId}' is not registered.";

        if (displayNameMatches.Count > 1)
        {
            var candidates = string.Join(", ", displayNameMatches
                .Select(d => $"'{d.AgentId.Value}'")
                .Order(StringComparer.Ordinal));
            return $"{prefix} Multiple registered agents have that display name: {candidates}. "
                + "agent_converse resolves an unambiguous display name, but this one is ambiguous - specify one of those ids.";
        }

        return $"{prefix} No registered agent has that id, and none has it as a display name either - "
            + "this is a target-resolution failure, not a policy denial. Call list_agents for the valid agent ids.";
    }

    /// <summary>
    /// Returns every registered agent whose <c>DisplayName</c> equals <paramref name="targetId"/>
    /// case-insensitively (#2878).
    /// </summary>
    /// <remarks>
    /// Defensive against a registry stub that returns no collection: target resolution must never be
    /// the thing that throws instead of reporting a resolution failure.
    /// </remarks>
    private List<AgentDescriptor> FindByDisplayName(AgentId targetId) =>
        [.. (_registry.GetAll() ?? [])
            .Where(d => string.Equals(d.DisplayName, targetId.Value, StringComparison.OrdinalIgnoreCase))];

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
