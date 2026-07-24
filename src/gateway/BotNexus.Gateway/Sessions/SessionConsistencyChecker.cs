using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// A single detected session/conversation consistency discrepancy and the disposition
/// taken for it. Emitted by <see cref="SessionConsistencyChecker"/> for structured
/// logging and operator inspection (issue #2046).
/// </summary>
/// <param name="Invariant">Stable machine name of the violated invariant.</param>
/// <param name="ConversationId">Conversation involved, when applicable.</param>
/// <param name="SessionId">Session involved, when applicable.</param>
/// <param name="PreviousState">Human-readable snapshot of the state before repair.</param>
/// <param name="Repair">Human-readable description of the repair (or why none was taken).</param>
/// <param name="Repaired">
/// <c>true</c> when a mutation was actually applied; <c>false</c> for report-only
/// (dry-run, non-recoverable, or guarded by a live turn).
/// </param>
public sealed record SessionConsistencyDiscrepancy(
    string Invariant,
    string? ConversationId,
    string? SessionId,
    string PreviousState,
    string Repair,
    bool Repaired);

/// <summary>
/// Aggregate outcome of a single consistency pass.
/// </summary>
/// <param name="ConversationsScanned">Number of conversations examined.</param>
/// <param name="SessionsScanned">Number of sessions examined for the stale-active-cron sweep.</param>
/// <param name="Discrepancies">Every discrepancy detected during the pass.</param>
public sealed record SessionConsistencyReport(
    int ConversationsScanned,
    int SessionsScanned,
    IReadOnlyList<SessionConsistencyDiscrepancy> Discrepancies)
{
    /// <summary>Number of discrepancies that were actually repaired in this pass.</summary>
    public int RepairedCount => Discrepancies.Count(d => d.Repaired);
}

/// <summary>
/// Detects and, when safe, repairs persisted session/conversation lifecycle
/// discrepancies through the supported store APIs - never raw SQL (issue #2046).
/// </summary>
/// <remarks>
/// <para>Invariants checked per pass:</para>
/// <list type="bullet">
///   <item><b>active-session-missing</b> - a conversation's <c>ActiveSessionId</c> references a
///     session that no longer exists. Repair: clear the pointer so the router mints a fresh
///     session on the next inbound.</item>
///   <item><b>active-session-cron-poison</b> - a non-cron (human/agent) conversation points at a
///     cron session while a more-recent non-cron session exists. Repair: re-point to the latest
///     non-cron session. Guarded by <see cref="ISessionTurnTracker"/>: if a turn is genuinely live
///     on the poisoned pointer, the discrepancy is reported but not repaired.</item>
///   <item><b>stale-active-cron</b> - a cron session left <c>Active</c> past a conservative
///     threshold with no live turn. Repair: seal it via the session store.</item>
/// </list>
/// <para>
/// Every repair is deterministic, idempotent, and bounded; running the pass repeatedly on an
/// already-consistent world detects nothing and mutates nothing.
/// </para>
/// </remarks>
public sealed class SessionConsistencyChecker(
    IConversationStore conversations,
    ISessionStore sessions,
    IOptions<SessionConsistencyOptions> optionsAccessor,
    ILogger<SessionConsistencyChecker> logger,
    ISessionTurnTracker? turnTracker = null,
    TimeProvider? timeProvider = null)
{
    private readonly IConversationStore _conversations = conversations;
    private readonly ISessionStore _sessions = sessions;
    private readonly ILogger<SessionConsistencyChecker> _logger = logger;
    private readonly ISessionTurnTracker? _turnTracker = turnTracker;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private SessionConsistencyOptions Options => optionsAccessor.Value;

    /// <summary>
    /// Runs one consistency pass. When <paramref name="dryRun"/> is <c>true</c> (or the configured
    /// <see cref="SessionConsistencyOptions.DryRun"/> is set) discrepancies are detected and
    /// reported but never mutated.
    /// </summary>
    /// <param name="dryRun">Override to force report-only for this call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SessionConsistencyReport> RunOnceAsync(
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var options = Options;
        var effectiveDryRun = dryRun || options.DryRun;
        var now = _timeProvider.GetUtcNow();
        var discrepancies = new List<SessionConsistencyDiscrepancy>();

        var allConversations = await _conversations.ListAsync(null, cancellationToken).ConfigureAwait(false);
        var conversationBudget = options.MaxConversationsPerRun > 0
            ? options.MaxConversationsPerRun
            : int.MaxValue;

        var conversationsScanned = 0;
        foreach (var conversation in allConversations)
        {
            if (conversationsScanned >= conversationBudget)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            conversationsScanned++;

            var found = await CheckConversationAsync(conversation, effectiveDryRun, cancellationToken).ConfigureAwait(false);
            if (found is not null)
                discrepancies.Add(found);
        }

        // Stale-active-cron sweep across all sessions.
        var sessionsScanned = 0;
        var allSessions = await _sessions.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var session in allSessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sessionsScanned++;

            var found = await CheckStaleActiveCronAsync(session, now, effectiveDryRun, cancellationToken).ConfigureAwait(false);
            if (found is not null)
                discrepancies.Add(found);
        }

        if (discrepancies.Count > 0)
        {
            _logger.LogInformation(
                "Session consistency pass complete: scanned {Conversations} conversations and {Sessions} sessions; {Detected} discrepancies detected, {Repaired} repaired (dryRun={DryRun}).",
                conversationsScanned, sessionsScanned, discrepancies.Count, discrepancies.Count(d => d.Repaired), effectiveDryRun);
        }

        return new SessionConsistencyReport(conversationsScanned, sessionsScanned, discrepancies);
    }

    private async Task<SessionConsistencyDiscrepancy?> CheckConversationAsync(
        Conversation conversation,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (conversation.ActiveSessionId is not { } activeSessionId)
            return null;

        var active = await _sessions.GetAsync(activeSessionId, cancellationToken).ConfigureAwait(false);

        // Invariant: active-session-missing. Pointer references a session that no longer exists.
        if (active is null)
        {
            var previous = $"ActiveSessionId={activeSessionId.Value} (not found in store)";
            if (dryRun)
            {
                LogDiscrepancy("active-session-missing", conversation.ConversationId.Value, activeSessionId.Value, previous, "would clear dangling pointer");
                return new SessionConsistencyDiscrepancy("active-session-missing", conversation.ConversationId.Value, activeSessionId.Value, previous, "clear dangling pointer", false);
            }

            conversation.ActiveSessionId = null;
            conversation.UpdatedAt = _timeProvider.GetUtcNow();
            await _conversations.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
            LogDiscrepancy("active-session-missing", conversation.ConversationId.Value, activeSessionId.Value, previous, "cleared dangling pointer");
            return new SessionConsistencyDiscrepancy("active-session-missing", conversation.ConversationId.Value, activeSessionId.Value, previous, "cleared dangling pointer", true);
        }

        // Invariant: active-session-cron-poison. A non-cron conversation points at a cron session
        // while a more-recent non-cron session exists in the same conversation.
        if (activeSessionId.IsCron)
        {
            var candidate = await ResolveLatestNonCronSessionAsync(conversation.ConversationId, cancellationToken).ConfigureAwait(false);
            if (candidate is null)
                return null; // Nothing unambiguous to restore; leave the pointer untouched.

            var previous = $"ActiveSessionId={activeSessionId.Value} (cron); latestNonCron={candidate.SessionId.Value}";

            // Guard: never re-point while a turn is genuinely executing on the poisoned pointer.
            if (_turnTracker?.HasLiveTurn(activeSessionId.Value) == true)
            {
                LogDiscrepancy("active-session-cron-poison", conversation.ConversationId.Value, activeSessionId.Value, previous, "skipped: live turn on active session");
                return new SessionConsistencyDiscrepancy("active-session-cron-poison", conversation.ConversationId.Value, activeSessionId.Value, previous, "skipped: live turn on active session", false);
            }

            if (dryRun)
            {
                LogDiscrepancy("active-session-cron-poison", conversation.ConversationId.Value, activeSessionId.Value, previous, $"would re-point to {candidate.SessionId.Value}");
                return new SessionConsistencyDiscrepancy("active-session-cron-poison", conversation.ConversationId.Value, activeSessionId.Value, previous, $"re-point to {candidate.SessionId.Value}", false);
            }

            conversation.ActiveSessionId = candidate.SessionId;
            conversation.UpdatedAt = _timeProvider.GetUtcNow();
            await _conversations.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
            LogDiscrepancy("active-session-cron-poison", conversation.ConversationId.Value, activeSessionId.Value, previous, $"re-pointed to {candidate.SessionId.Value}");
            return new SessionConsistencyDiscrepancy("active-session-cron-poison", conversation.ConversationId.Value, activeSessionId.Value, previous, $"re-pointed to {candidate.SessionId.Value}", true);
        }

        return null;
    }

    // Latest non-cron, non-sealed session for the conversation, preferring the most recently
    // created. Returns null when there is no unambiguous channel-compatible candidate.
    private async Task<GatewaySession?> ResolveLatestNonCronSessionAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken)
    {
        var members = await _sessions.ListByConversationAsync(conversationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return members
            .Where(s => !s.SessionId.IsCron && s.Status != SessionStatus.Sealed)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.UpdatedAt)
            .FirstOrDefault();
    }

    private async Task<SessionConsistencyDiscrepancy?> CheckStaleActiveCronAsync(
        GatewaySession session,
        DateTimeOffset now,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!session.SessionId.IsCron || session.Status != SessionStatus.Active)
            return null;

        var threshold = Options.StaleActiveCronThreshold;
        if (threshold <= TimeSpan.Zero || now - session.UpdatedAt <= threshold)
            return null;

        // Never terminalize a cron session that still has a live turn.
        if (_turnTracker?.HasLiveTurn(session.SessionId.Value) == true)
            return null;

        var previous = $"cron session Active, updatedAt={session.UpdatedAt:O}";

        if (dryRun)
        {
            LogDiscrepancy("stale-active-cron", session.ConversationId.Value, session.SessionId.Value, previous, "would seal stale active cron session");
            return new SessionConsistencyDiscrepancy("stale-active-cron", session.ConversationId.Value, session.SessionId.Value, previous, "seal stale active cron session", false);
        }

        session.Status = SessionStatus.Sealed;
        session.UpdatedAt = _timeProvider.GetUtcNow();
        await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        LogDiscrepancy("stale-active-cron", session.ConversationId.Value, session.SessionId.Value, previous, "sealed stale active cron session");
        return new SessionConsistencyDiscrepancy("stale-active-cron", session.ConversationId.Value, session.SessionId.Value, previous, "sealed stale active cron session", true);
    }

    private void LogDiscrepancy(string invariant, string? conversationId, string? sessionId, string previousState, string repair)
    {
        _logger.LogInformation(
            "Session consistency discrepancy {Invariant}: conversation={ConversationId} session={SessionId} previous=[{PreviousState}] disposition=[{Repair}].",
            invariant, conversationId, sessionId, previousState, repair);
    }
}
