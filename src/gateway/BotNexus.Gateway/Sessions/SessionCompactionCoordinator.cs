using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Sessions;

/// <inheritdoc />
public sealed class SessionCompactionCoordinator : ISessionCompactionCoordinator
{
    private readonly ISessionCompactor _compactor;
    private readonly ISessionStore _sessions;
    private readonly IAgentSupervisor _supervisor;
    private readonly IChannelManager _channelManager;
    private readonly IOptionsMonitor<CompactionOptions> _options;
    private readonly IPreCompactionMemoryFlusher? _memoryFlusher;
    private readonly ILogger<SessionCompactionCoordinator> _logger;

    public SessionCompactionCoordinator(
        ISessionCompactor compactor,
        ISessionStore sessions,
        IAgentSupervisor supervisor,
        IChannelManager channelManager,
        IOptionsMonitor<CompactionOptions> options,
        ILogger<SessionCompactionCoordinator> logger,
        IPreCompactionMemoryFlusher? memoryFlusher = null)
    {
        _compactor = compactor;
        _sessions = sessions;
        _supervisor = supervisor;
        _channelManager = channelManager;
        _options = options;
        _logger = logger;
        _memoryFlusher = memoryFlusher;
    }

    public async Task<SessionCompactionOutcome> CompactAsync(
        AgentId agentId,
        GatewaySession session,
        CancellationToken cancellationToken,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        var options = _options.CurrentValue;

        // When the user explicitly requests compaction, override PreservedTurns
        // to 0 so the compactor always has entries to summarise regardless of
        // how few user turns exist. The user's intent ("compact now") supersedes
        // the automatic heuristic. PreservedTurns=0 causes SplitHistory to place
        // ALL visible entries in toSummarize (the summary captures full context
        // including the most recent exchange).
        if (force)
        {
            options = options with { PreservedTurns = 0 };
        }
        var sessionId = session.SessionId;

        // #1518: capture the run's authoritative session identity BEFORE the (potentially
        // long-running) summarization call. The post-run persistence at step 3 is then fenced
        // against it so a delete/reset that lands while we are summarising cannot be undone by
        // the compaction record write resurrecting or un-sealing the row.
        var fence = SessionWriteFence.Capture(session);

        // 1. Optional pre-compaction memory flush. Best-effort: failures are logged
        //    and swallowed so compaction always proceeds (mirrors auto-compact path).
        if (_memoryFlusher is not null && _memoryFlusher.ShouldFlush(session.Session, options))
        {
            try
            {
                await _memoryFlusher.FlushAsync(agentId, session.Session, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pre-compaction memory flush failed for session {SessionId}; compaction will proceed.", sessionId);
            }
        }

        // 2. Summarise older history. Snapshots inside the compactor so the result
        //    can be applied with optimistic-concurrency detection.
        CompactionResult result;
        try
        {
            result = await _compactor.CompactAsync(session, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // #3362: attribute the failure instead of collapsing it. A read-shaped fault (I/O,
            // permissions, deserialization, store unavailable) is NOT a summarization failure, and
            // the exception type is carried through so the true fault is diagnosable.
            var thrownSkipReason = ClassifyThrownException(ex);
            _logger.LogWarning(
                ex,
                "Compaction call failed for session {SessionId} ({ExceptionType}, reason={SkipReason}); history unchanged.",
                sessionId, ex.GetType().Name, thrownSkipReason);
            return new SessionCompactionOutcome(
                Succeeded: false,
                Applied: false,
                HistoryOutcome: HistoryReplaceOutcome.Aborted,
                EntriesSummarized: 0,
                EntriesPreserved: 0,
                TokensBefore: 0,
                TokensAfter: 0,
                FailureReason: DescribeFailure(thrownSkipReason, ex),
                SkipReason: thrownSkipReason,
                FailureExceptionType: ex.GetType().Name);
        }

        // 3. Apply + persist. Track outcome for the caller.
        var historyOutcome = HistoryReplaceOutcome.Aborted;
        var applied = false;
        if (result.Succeeded && result.CompactedHistory is not null)
        {
            historyOutcome = session.TryApplyCompactionResult(result);
            switch (historyOutcome)
            {
                case HistoryReplaceOutcome.Applied:
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    applied = true;
                    break;
                case HistoryReplaceOutcome.Rebased:
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    applied = true;
                    _logger.LogInformation(
                        "Session {SessionId} compaction rebased over concurrent additions; TokensAfter is approximate.",
                        sessionId);
                    break;
                case HistoryReplaceOutcome.Aborted:
                    _logger.LogWarning(
                        "Session {SessionId} compaction aborted: {Reason} — history was destructively modified during the summary call. History is unchanged.",
                        sessionId, CompactionSkipReason.ConcurrentHistoryChange);
                    break;
            }
        }

        var saveOutcome = await _sessions.SaveAsync(session, fence, cancellationToken).ConfigureAwait(false);
        if (saveOutcome == SessionSaveOutcome.Rebound)
        {
            // #1518: the session was deleted, sealed, or rebound while we summarised. Do NOT
            // resurrect it and do NOT evict the agent handle (there is nothing to rebuild) -
            // surface an aborted outcome so channel callers see the compaction did not land.
            _logger.LogInformation(
                "Session {SessionId} compaction discarded as rebound: the session was deleted or reset " +
                "while compaction was in flight; the compaction record was not persisted (#1518).",
                sessionId);
            return new SessionCompactionOutcome(
                Succeeded: result.Succeeded,
                Applied: false,
                HistoryOutcome: HistoryReplaceOutcome.Aborted,
                EntriesSummarized: result.EntriesSummarized,
                EntriesPreserved: result.EntriesPreserved,
                TokensBefore: result.TokensBefore,
                TokensAfter: result.TokensAfter,
                FailureReason: "Compaction was discarded because the session was deleted or reset while it was in progress.",
                SkipReason: CompactionSkipReason.SessionRebound);
        }

        // #2460: the Aborted outcome previously logged NO reason, making a repeating no-op abort
        // loop undiagnosable from logs alone. Resolve the specific branch and log it at Warning
        // (survives production log settings) whenever nothing was applied.
        var skipReason = !applied
            ? result.SkipReason
              ?? (historyOutcome == HistoryReplaceOutcome.Aborted
                  ? CompactionSkipReason.ConcurrentHistoryChange
                  : null)
            : null;

        if (!applied)
        {
            _logger.LogWarning(
                "Session {SessionId} compaction did not apply: reason={SkipReason}, {Summarized} entries summarized, " +
                "{Preserved} preserved, tokens {TokensBefore}->{TokensAfter} (outcome={Outcome}). History is unchanged.",
                sessionId,
                skipReason ?? "Unspecified",
                result.EntriesSummarized,
                result.EntriesPreserved,
                result.TokensBefore,
                result.TokensAfter,
                historyOutcome);
        }

        _logger.LogInformation(
            "Session {SessionId} compacted: {Summarized} entries summarized, {Preserved} preserved (applied={Applied}, outcome={Outcome}, reason={SkipReason}).",
            sessionId, result.EntriesSummarized, result.EntriesPreserved, applied, historyOutcome, skipReason ?? "(none)");

        // 4. Evict the cached agent handle on success so the next turn rebuilds
        //    context from post-compaction history (PR #602 Bug 3 fix — must run
        //    on every compaction path, not just auto-compact).
        if (applied)
        {
            try
            {
                await _supervisor.StopAsync(agentId, sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evict cached agent handle for session {SessionId} after compaction.", sessionId);
            }
        }

        // #3362: the reason is DERIVED from the outcome. It used to be hard-coded to the
        // summarization-empty-response text for every non-success, which reported an I/O or
        // deserialization fault as a summarization-model fault that had not occurred.
        string? failureReason = null;
        if (!result.Succeeded)
            failureReason = DescribeFailure(skipReason, exception: null);
        else if (historyOutcome == HistoryReplaceOutcome.Aborted)
            failureReason = "Compaction conflicted with a concurrent change to the session. Session history was not modified — please try again.";

        return new SessionCompactionOutcome(
            Succeeded: result.Succeeded,
            Applied: applied,
            HistoryOutcome: historyOutcome,
            EntriesSummarized: result.EntriesSummarized,
            EntriesPreserved: result.EntriesPreserved,
            TokensBefore: result.TokensBefore,
            TokensAfter: result.TokensAfter,
            FailureReason: failureReason,
            SkipReason: skipReason);
    }

    /// <summary>
    /// #3362: maps an exception that escaped the compactor onto the skip reason that names what
    /// actually broke. Read-shaped faults (I/O, permissions, deserialization, disposed store) are
    /// <see cref="CompactionSkipReason.HistoryReadFailed"/>; everything else remains
    /// <see cref="CompactionSkipReason.SummarizationFailed"/>, so the pre-existing discriminator
    /// is narrowed rather than cannibalised.
    /// </summary>
    private static CompactionSkipReason ClassifyThrownException(Exception ex) => ex switch
    {
        IOException => CompactionSkipReason.HistoryReadFailed,
        UnauthorizedAccessException => CompactionSkipReason.HistoryReadFailed,
        System.Text.Json.JsonException => CompactionSkipReason.HistoryReadFailed,
        ObjectDisposedException => CompactionSkipReason.HistoryReadFailed,
        _ => CompactionSkipReason.SummarizationFailed,
    };

    /// <summary>
    /// #3362: derives the operator-visible message from the skip reason. The
    /// "summarization model returned an empty response" text is now reserved for
    /// <see cref="CompactionSkipReason.EmptySummary"/> — the one branch where that is what
    /// happened — and every other branch names its own cause.
    /// </summary>
    private static string DescribeFailure(CompactionSkipReason? reason, Exception? exception)
    {
        var detail = exception is null ? string.Empty : $" ({exception.GetType().Name}: {exception.Message})";

        if (reason is null)
            return $"Compaction did not complete and session history was not modified.{detail}";

        if (reason == CompactionSkipReason.EmptySummary)
            return "Compaction aborted: the summarization model returned an empty response. Session history was not modified.";

        if (reason == CompactionSkipReason.HistoryReadFailed)
            return $"Compaction aborted: the session history could not be read{detail}. Session history was not modified — this is a storage or transcript fault, not a summarization failure.";

        if (reason == CompactionSkipReason.EmptyHistory)
            return "Compaction skipped: the session history is empty, so there was nothing to summarise.";

        if (reason == CompactionSkipReason.NoSummarizableTurns)
            return "Compaction skipped: no summarizable turns were found. Session history was not modified.";

        if (reason == CompactionSkipReason.SummarizationTimeout)
            return "Compaction aborted: the summarization call timed out. Session history was not modified.";

        if (reason == CompactionSkipReason.SummarizationFailed)
            return $"Compaction aborted: the summarization call failed{detail}. Session history was not modified.";

        if (reason == CompactionSkipReason.CircuitBreakerOpen)
            return "Compaction skipped: the compaction circuit breaker is open after repeated failures. Session history was not modified.";

        if (reason == CompactionSkipReason.ConcurrentHistoryChange)
            return "Compaction conflicted with a concurrent change to the session. Session history was not modified — please try again.";

        if (reason == CompactionSkipReason.SessionRebound)
            return "Compaction was discarded because the session was deleted or reset while it was in progress.";

        // Forward-compatible: an unknown code from a newer build still names itself rather than
        // inheriting some other branch's story.
        return $"Compaction did not complete (reason={reason}). Session history was not modified.{detail}";
    }

    public string BuildNotificationText(SessionCompactionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.FailureReason is { } reason)
            return reason;

        var rebased = outcome.HistoryOutcome == HistoryReplaceOutcome.Rebased
            ? " (rebased over concurrent additions)"
            : string.Empty;
        return $"_[Session context compacted: {outcome.EntriesSummarized} older messages summarised, {outcome.EntriesPreserved} recent messages preserved{rebased}. Continuing…]_";
    }

    public async Task<bool> TrySendChannelNotificationAsync(
        SessionCompactionOutcome outcome,
        ChannelKey channelType,
        ChannelAddress channelAddress,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var adapter = _channelManager.Get(channelType);
        if (adapter is null)
        {
            // #3541: a non-deliverable channel type (cron / exchange / webhook) has no adapter by
            // design, so "notification dropped" is the expected outcome, not a fault. Reached
            // through the single OutboundResponseDeliverer seam rather than a local copy of the set.
            if (OutboundResponseDeliverer.IsNonDeliverableChannel(channelType))
            {
                _logger.LogDebug("Skipping compaction notification for non-deliverable channel type '{ChannelType}' (session {SessionId}).", channelType, sessionId);
                return false;
            }

            _logger.LogWarning("No channel adapter found for type '{ChannelType}' — compaction notification dropped for session {SessionId}.", channelType, sessionId);
            return false;
        }

        try
        {
            await adapter.SendAsync(new OutboundMessage
            {
                ChannelType = channelType,
                ChannelAddress = channelAddress,
                Content = BuildNotificationText(outcome),
                SessionId = sessionId
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send compaction notification for session {SessionId}.", sessionId);
            return false;
        }
    }
}
