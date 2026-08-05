using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Ralph;

/// <summary>
/// Drives a ralph conversation's loop off the <em>turn-end</em> seam (issue #2818).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a subscriber and not a change to the seam.</b> The gateway already publishes
/// <see cref="SessionLifecycleEventType.Closed"/> from the streaming post-run finalizer at the end of
/// every streamed turn. (The enum name is actively misleading - #2780 established that it fires per
/// <em>turn</em>, not per session, and reading <c>StreamingSessionHelper</c> confirms it: the publish
/// sits in the final-write block of a single run, gated on the save having persisted.) Because the
/// event already exists, a ralph loop is one additional subscriber and needs no edit to the seam, no
/// timer, and therefore has no missed-wake failure class: a turn that is still running has not
/// published yet, so a loop with an in-flight sub-agent simply does not re-trigger until the turn
/// actually ends (acceptance criterion 10).
/// </para>
/// <para>
/// <b>One decision, made elsewhere.</b> This type contains no stop logic of its own. Whether the loop
/// continues is asked of <see cref="RalphLoopPolicy.Evaluate"/> exactly once per turn end, and the
/// answer - continue, or a named stop reason - is what this type acts on and records. Adding an
/// <c>if</c> here would create a second spelling of "is this loop done" that drifts from the first.
/// </para>
/// <para>
/// <b>The kill switch does not wait for the in-flight turn.</b> <see cref="Kill"/> both marks the
/// durable state and cancels the loop's live token, so an in-flight iteration is cancelled rather
/// than awaited (criterion 9).
/// </para>
/// </remarks>
public sealed class RalphLoopTrigger : IHostedService
{
    private readonly ISessionLifecycleEvents? _lifecycleEvents;
    private readonly IConversationStore _conversations;
    private readonly IRalphIterationRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RalphLoopTrigger> _logger;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _live = new(StringComparer.Ordinal);

    /// <summary>Creates the trigger.</summary>
    public RalphLoopTrigger(
        IConversationStore conversations,
        IRalphIterationRunner runner,
        ILogger<RalphLoopTrigger> logger,
        ISessionLifecycleEvents? lifecycleEvents = null,
        TimeProvider? timeProvider = null)
    {
        _conversations = conversations;
        _runner = runner;
        _logger = logger;
        _lifecycleEvents = lifecycleEvents;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_lifecycleEvents is not null)
            _lifecycleEvents.SessionChanged += OnSessionChangedAsync;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifecycleEvents is not null)
            _lifecycleEvents.SessionChanged -= OnSessionChangedAsync;

        foreach (var cts in _live.Values)
            cts.Cancel();

        return Task.CompletedTask;
    }

    /// <summary>
    /// The turn-end handler. Public so tests can drive the seam directly without standing up a
    /// streaming run; production wiring goes through <see cref="StartAsync"/>.
    /// </summary>
    public async Task OnSessionChangedAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        // Only turn end re-triggers. Created/MessageAdded/Expired/Deleted are not turn boundaries.
        if (lifecycleEvent.Type != SessionLifecycleEventType.Closed)
            return;

        var conversationId = lifecycleEvent.Session?.ConversationId;
        if (conversationId is not { } id || string.IsNullOrWhiteSpace(id.Value))
            return;

        await AdvanceAsync(id, turnSucceeded: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signals that the agent asked the loop to pause ("nothing to do"). Recorded durably so a paused
    /// loop is distinguishable from a running one and from a stopped one via the conversation's
    /// recorded stop reason, and survives a gateway restart.
    /// </summary>
    public Task PauseAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
        => MutateStateAsync(
            conversationId,
            state => state with { IsPaused = true },
            cancellationToken);

    /// <summary>
    /// Clears pause and failure state so the loop may run again. Resuming is always explicit: a paused
    /// or circuit-broken loop never restarts itself, because the condition that stopped it is exactly
    /// the condition that would immediately stop it again.
    /// </summary>
    public Task ResumeAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
        => MutateStateAsync(
            conversationId,
            state => state with
            {
                IsPaused = false,
                IsKilled = false,
                ConsecutiveFailures = 0,
                StopReason = RalphStopReason.None,
                StopDetail = null
            },
            cancellationToken);

    /// <summary>
    /// Throws the external kill switch. Marks the durable state <em>and</em> cancels the in-flight
    /// iteration, so stopping does not wait for the current turn to end (criterion 9).
    /// </summary>
    public async Task KillAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        Kill(conversationId);
        await MutateStateAsync(
            conversationId,
            state => state with { IsKilled = true },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels any in-flight iteration for the conversation without touching durable state.</summary>
    public void Kill(ConversationId conversationId)
    {
        if (_live.TryRemove(conversationId.Value, out var cts))
            cts.Cancel();
    }

    /// <summary>
    /// Evaluates the single stop decision for a conversation and, when it says continue, runs exactly
    /// one iteration. Whatever the decision, the resulting state - including a named stop reason when
    /// it halted - is written back to the conversation so the halt is disclosed rather than silent.
    /// </summary>
    /// <param name="conversationId">The conversation to advance.</param>
    /// <param name="turnSucceeded">Whether the turn that just ended succeeded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision that was acted on.</returns>
    public async Task<RalphLoopDecision> AdvanceAsync(
        ConversationId conversationId,
        bool turnSucceeded,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return RalphLoopDecision.Stop(RalphStopReason.NotActive, "Conversation no longer exists.");

        if (conversation.Kind != ConversationKind.Ralph)
            return RalphLoopDecision.Stop(RalphStopReason.NotRalph, "Conversation kind is not 'ralph'; it never re-triggers.");

        var (config, state) = RalphLoopMetadata.Read(conversation);
        var now = _timeProvider.GetUtcNow();

        state = state with
        {
            StartedAt = state.StartedAt ?? now,
            ConsecutiveFailures = turnSucceeded ? 0 : state.ConsecutiveFailures + 1
        };

        // THE decision. One call, one place, one result carrying outcome and reason.
        var decision = RalphLoopPolicy.Evaluate(
            conversation.Kind,
            conversation.Status,
            conversation.Instructions,
            config,
            state,
            now);

        if (!decision.ShouldContinue)
        {
            state = state with { StopReason = decision.Reason, StopDetail = decision.Detail };
            await PersistAsync(conversation, config, state, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Ralph loop for conversation '{ConversationId}' stopped: {Reason} - {Detail}",
                conversationId,
                decision.Reason,
                decision.Detail);
            return decision;
        }

        var iteration = state.Iterations + 1;
        state = state with { Iterations = iteration, StopReason = RalphStopReason.None, StopDetail = null };
        await PersistAsync(conversation, config, state, cancellationToken).ConfigureAwait(false);

        var prompt = conversation.Instructions!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _live[conversationId.Value] = cts;
        try
        {
            var succeeded = await _runner
                .RunIterationAsync(conversation, prompt, iteration, cts.Token)
                .ConfigureAwait(false);

            if (!succeeded)
            {
                // Record the failure immediately so the circuit breaker counts it even if this
                // iteration never reaches the turn-end seam (a failed turn may not publish).
                await MutateStateAsync(
                    conversationId,
                    current => current with { ConsecutiveFailures = current.ConsecutiveFailures + 1 },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _live.TryRemove(new KeyValuePair<string, CancellationTokenSource>(conversationId.Value, cts));
        }

        return decision;
    }

    private async Task MutateStateAsync(
        ConversationId conversationId,
        Func<RalphLoopState, RalphLoopState> mutate,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return;

        var (config, state) = RalphLoopMetadata.Read(conversation);
        await PersistAsync(conversation, config, mutate(state), cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistAsync(
        Conversation conversation,
        RalphLoopConfig config,
        RalphLoopState state,
        CancellationToken cancellationToken)
    {
        RalphLoopMetadata.Write(conversation, config, state);
        conversation.UpdatedAt = _timeProvider.GetUtcNow();
        await _conversations.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
    }
}
