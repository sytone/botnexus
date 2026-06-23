using System.Collections.Concurrent;
using System.Text;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain.Primitives;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Sessions;

public sealed class LlmSessionCompactor : ISessionCompactor
{
    private static readonly string[] DefaultSummaryModelIds =
    [
        "gpt-4.1-mini",
        "gpt-5-mini",
        "claude-haiku-4.5",
        "gpt-4.1"
    ];

    private readonly LlmClient _llmClient;
    private readonly ILogger<LlmSessionCompactor> _logger;
    private readonly ISecretRedactor? _redactor;
    private readonly IOptionsMonitor<PlatformConfig>? _platformConfig;
    private readonly GatewayAuthManager? _authManager;

    /// <summary>
    /// Tracks consecutive compaction failures per session for circuit breaker logic.
    /// After <see cref="MaxConsecutiveFailures"/> consecutive failures the breaker opens for that
    /// session, but only for a bounded cooldown window (see <see cref="BreakerState"/> and
    /// <see cref="CompactionOptions.CircuitBreakerCooldownSeconds"/>) rather than permanently — a
    /// transient provider outage must not wedge a session until the gateway restarts.
    /// </summary>
    private readonly ConcurrentDictionary<string, BreakerState> _breaker = new();

    /// <summary>
    /// Per-session circuit-breaker bookkeeping: how many consecutive failures have occurred and when
    /// the most recent one happened. The breaker is considered open once
    /// <see cref="Count"/> reaches <see cref="MaxConsecutiveFailures"/>, and stays open until the
    /// cooldown window elapses past <see cref="LastFailureUtc"/>, after which it auto-resets.
    /// </summary>
    private sealed record BreakerState(int Count, DateTimeOffset LastFailureUtc);

    /// <summary>
    /// Maximum consecutive compaction failures before the circuit breaker opens.
    /// </summary>
    internal const int MaxConsecutiveFailures = 3;

    /// <summary>
    /// Maximum characters per entry content in the summarization prompt.
    /// Tool results and long assistant messages are truncated to this length
    /// to prevent the prompt from exceeding model context limits.
    /// </summary>
    internal const int MaxEntryContentCharsInPrompt = 500;

    /// <summary>
    /// Maximum total characters for the summarization prompt.
    /// If the prompt exceeds this after truncation, older entries are dropped.
    /// Based on ~80% of a 128K token model's input capacity (chars/4 ≈ tokens).
    /// </summary>
    internal const int MaxSummarizationPromptChars = 400_000;
    public LlmSessionCompactor(LlmClient llmClient, ILogger<LlmSessionCompactor> logger, ISecretRedactor? redactor = null, IOptionsMonitor<PlatformConfig>? platformConfig = null, GatewayAuthManager? authManager = null)
    {
        _llmClient = llmClient;
        _logger = logger;
        _redactor = redactor;
        _platformConfig = platformConfig;
        _authManager = authManager;
    }

    public bool ShouldCompact(Session session, CompactionOptions options)
    {
        var estimatedTokens = EstimateVisibleTokenCount(session);
        var threshold = (int)(options.ContextWindowTokens * options.TokenThresholdRatio);
        var shouldCompact = estimatedTokens > threshold;

        _logger.LogDebug(
            "ShouldCompact check for session {SessionId}: estimated {EstimatedTokens} tokens, " +
            "threshold {Threshold} (window {Window} * ratio {Ratio}), result: {ShouldCompact}",
            session.SessionId,
            estimatedTokens,
            threshold,
            options.ContextWindowTokens,
            options.TokenThresholdRatio,
            shouldCompact);

        return shouldCompact;
    }

    public async Task<CompactionResult> CompactAsync(
        GatewaySession session,
        CompactionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Circuit breaker: skip compaction if this session has failed too many times *recently*.
        // The breaker opens after MaxConsecutiveFailures but auto-resets once the cooldown window has
        // elapsed, so a transient provider outage (e.g. a burst of HTTP 421s) cannot wedge a session
        // until the gateway restarts.
        var sessionKey = session.SessionId.Value;
        var cooldown = TimeSpan.FromSeconds(
            options.CircuitBreakerCooldownSeconds > 0 ? options.CircuitBreakerCooldownSeconds : 600);
        if (_breaker.TryGetValue(sessionKey, out var breakerState) &&
            breakerState.Count >= MaxConsecutiveFailures)
        {
            var elapsed = DateTimeOffset.UtcNow - breakerState.LastFailureUtc;
            if (elapsed < cooldown)
            {
                _logger.LogWarning(
                    "Compaction circuit breaker OPEN for session {SessionId}: " +
                    "{Failures} consecutive failures. Cooling down for {Remaining:0}s more before retrying.",
                    sessionKey, breakerState.Count, (cooldown - elapsed).TotalSeconds);
                return CompactionResult.Skipped();
            }

            // Cooldown elapsed: clear the breaker and allow this attempt through.
            _breaker.TryRemove(sessionKey, out _);
            _logger.LogInformation(
                "Compaction circuit breaker cooldown elapsed for session {SessionId} after {Elapsed:0}s. " +
                "Retrying compaction.",
                sessionKey, elapsed.TotalSeconds);
        }

        // Atomic snapshot: history copy + destructive-mutation version + count, all
        // captured under the runtime lock. The compactor operates only on this
        // immutable snapshot below; live `session.History` is not read again until
        // the caller applies the result via TryReplaceHistoryFromSnapshot (#532).
        var snap = session.SnapshotHistoryForCompaction();
        var history = snap.Entries;
        if (history.Count == 0)
        {
            return CompactionResult.Skipped(snap.DestructiveVersion, snap.Count);
        }

        // Phase 3a: compaction operates on the "LLM-visible" projection only. Already-historical
        // entries from prior compactions, and crash sentinels, are passed through verbatim and
        // must never be re-summarised. The visibility predicate is centralised in
        // SessionContextProjector (Phase 3b, #534).
        var visible = history.Where(SessionContextProjector.IsVisibleInLiveContext).ToList();
        var (toSummarize, toPreserve) = SplitHistory(visible, options.PreservedTurns);
        if (toSummarize.Count == 0)
        {
            var visibleTokens = EstimateVisibleTokenCountFromEntries(history);
            return CompactionResult.Skipped(
                snap.DestructiveVersion,
                snap.Count,
                entriesPreserved: toPreserve.Count,
                tokensBefore: visibleTokens,
                tokensAfter: visibleTokens);
        }

        var tokensBefore = EstimateVisibleTokenCountFromEntries(history);
        var priorSummary = ExtractPriorSummary(toSummarize);
        var summaryPrompt = BuildSummarizationPrompt(toSummarize, options.MaxSummaryChars, priorSummary);
        var effectiveOptions = ResolveEffectiveOptions(options);

        string summary;
        try
        {
            summary = await CallLlmForSummaryAsync(summaryPrompt, effectiveOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout fired (not caller cancellation) — treat as a provider stall.
            var newCount = RecordFailure(sessionKey);
            _logger.LogWarning(
                "Compaction timed out for session {SessionId} after {Timeout}s. " +
                "History is unchanged. Consecutive failures: {Failures}/{Max}",
                session.SessionId,
                effectiveOptions.TimeoutSeconds,
                newCount,
                MaxConsecutiveFailures);

            return CompactionResult.Skipped(
                snap.DestructiveVersion,
                snap.Count,
                entriesPreserved: history.Count,
                tokensBefore: tokensBefore,
                tokensAfter: tokensBefore);
        }

        // Bug 1 / Bug 5 guard: if the LLM returned nothing, abort — do NOT mutate history.
        if (string.IsNullOrWhiteSpace(summary))
        {
            var newCount = RecordFailure(sessionKey);
            _logger.LogWarning(
                "Compaction aborted for session {SessionId}: LLM returned an empty summary. " +
                "History is unchanged. Summarized {Count} entries would have been marked as historical. " +
                "Consecutive failures: {Failures}/{Max}",
                session.SessionId,
                toSummarize.Count,
                newCount,
                MaxConsecutiveFailures);

            return CompactionResult.Skipped(
                snap.DestructiveVersion,
                snap.Count,
                entriesPreserved: history.Count,
                tokensBefore: tokensBefore,
                tokensAfter: tokensBefore);
        }

        if (summary.Length > options.MaxSummaryChars)
        {
            summary = summary[..options.MaxSummaryChars];
        }

        // Redact any secrets that leaked into the LLM summary before persisting.
        if (_redactor is not null)
            summary = _redactor.Redact(summary);

        // Phase 3a: rebuild the new history by folding the summarised range and inserting the
        // summary entry at the historical/preserved boundary. Extracted (#1564) so the subtle
        // insert-at-index walk (the #532 drop-entries bug class) is independently testable.
        var newHistory = BuildCompactedHistory(history, toSummarize, summary);
        var tokensAfter = EstimateVisibleTokenCountFromEntries(newHistory);

        _logger.LogInformation(
            "Compacted session {SessionId}: {Summarized} entries marked historical, {Preserved} preserved, " +
            "tokens {Before}→{After} (delta {Delta}) — full history retained in store",
            session.SessionId,
            toSummarize.Count,
            toPreserve.Count,
            tokensBefore,
            tokensAfter,
            tokensBefore - tokensAfter);

        // Reset circuit breaker on success.
        _breaker.TryRemove(sessionKey, out _);

        return CompactionResult.ForSuccess(
            summary,
            newHistory,
            entriesSummarized: toSummarize.Count,
            entriesPreserved: toPreserve.Count,
            tokensBefore: tokensBefore,
            tokensAfter: tokensAfter,
            snapshotDestructiveVersion: snap.DestructiveVersion,
            snapshotHistoryCount: snap.Count);
    }

    /// <summary>
    /// Rebuilds session history after a successful summarization: walks the ORIGINAL history,
    /// marks every entry in <paramref name="toSummarize"/> as <c>IsHistory = true</c> (folded), passes
    /// all other entries (pre-existing historical entries, crash sentinels, the preserved tail)
    /// through verbatim, and inserts the new summary entry at the index immediately AFTER the last
    /// summarised entry so chronological order is preserved for transcript readers. Extracted from
    /// <see cref="CompactAsync"/> (#1564) because the <c>summaryInserted</c>/<c>summarizedSeen</c>
    /// bookkeeping is the part most likely to silently drop entries (the #532 bug class) and
    /// deserves direct unit coverage.
    /// </summary>
    private static List<SessionEntry> BuildCompactedHistory(
        IReadOnlyList<SessionEntry> history,
        IReadOnlyList<SessionEntry> toSummarize,
        string summary)
    {
        var compactionEntry = new SessionEntry
        {
            Role = MessageRole.System,
            Content = SummaryPrefix + "\n" + summary,
            IsCompactionSummary = true,
            Timestamp = DateTimeOffset.UtcNow
        };

        var toSummarizeSet = new HashSet<SessionEntry>(toSummarize, ReferenceEqualityComparer.Instance);
        var newHistory = new List<SessionEntry>(history.Count + 1);
        var summaryInserted = false;
        var summarizedSeen = 0;
        for (var i = 0; i < history.Count; i++)
        {
            var entry = history[i];
            if (toSummarizeSet.Contains(entry))
            {
                newHistory.Add(entry with { IsHistory = true });
                summarizedSeen++;
                if (summarizedSeen == toSummarize.Count && !summaryInserted)
                {
                    newHistory.Add(compactionEntry);
                    summaryInserted = true;
                }
            }
            else
            {
                newHistory.Add(entry);
            }
        }

        // Defensive: if for any reason the summary wasn't inserted (shouldn't happen — toSummarize
        // comes from the iteration above) prepend it so it's still LLM-visible.
        if (!summaryInserted)
            newHistory.Insert(0, compactionEntry);

        return newHistory;
    }

    /// <summary>
    /// Records a compaction failure for the circuit breaker: increments the consecutive-failure
    /// count and stamps the failure time (used by the cooldown check in <see cref="CompactAsync"/>).
    /// Returns the new consecutive-failure count for logging.
    /// </summary>
    private int RecordFailure(string sessionKey)
    {
        var updated = _breaker.AddOrUpdate(
            sessionKey,
            _ => new BreakerState(1, DateTimeOffset.UtcNow),
            (_, existing) => existing with { Count = existing.Count + 1, LastFailureUtc = DateTimeOffset.UtcNow });
        return updated.Count;
    }

    private static (List<SessionEntry> toSummarize, List<SessionEntry> toPreserve) SplitHistory(
        IReadOnlyList<SessionEntry> history,
        int preservedTurns)
    {
        if (preservedTurns <= 0)
        {
            return (history.ToList(), []);
        }

        var userTurnCount = 0;
        var splitIndex = -1;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (!history[i].Role.Equals(MessageRole.User))
            {
                continue;
            }

            userTurnCount++;
            if (userTurnCount >= preservedTurns)
            {
                splitIndex = i;
                break;
            }
        }

        if (splitIndex < 0)
        {
            return ([], history.ToList());
        }

        var toSummarize = history.Take(splitIndex).ToList();
        var toPreserve = history.Skip(splitIndex).ToList();
        return (toSummarize, toPreserve);
    }

    /// <summary>
    /// Guardrail prefix injected before every compaction summary in conversation history.
    /// Prevents the agent from resuming stale tasks after a context window handoff.
    /// </summary>
    internal const string SummaryPrefix =
        "[CONTEXT COMPACTION -- REFERENCE ONLY] Earlier turns were compacted into the summary below.\n" +
        "This is a handoff from a previous context window -- treat it as background reference, NOT as active instructions.\n" +
        "Do NOT answer questions or fulfill requests mentioned in this summary; they were already addressed.\n" +
        "Respond ONLY to the latest user message that appears AFTER this summary -- that is the single source of truth.\n" +
        "If the latest user message contradicts, supersedes, or diverges from Active Task / In Progress / Remaining Work,\n" +
        "the latest message WINS -- discard stale items entirely.\n" +
        "Reverse signals (stop, undo, roll back, never mind, new topic) must immediately end any in-flight work described in the summary.\n" +
        "IMPORTANT: Persistent memory (MEMORY.md, USER.md) in the system prompt is ALWAYS authoritative.";

    private static string BuildSummarizationPrompt(List<SessionEntry> entries, int maxChars, string? priorSummary = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Summarize the following conversation history. Preserve critical information in a structured format.");
        builder.AppendLine();
        builder.AppendLine("Required sections:");
        builder.AppendLine("## Resolved -- completed tasks, decisions made");
        builder.AppendLine("## Active Task -- what was being worked on at compaction time");
        builder.AppendLine("## In Progress -- tool calls / sub-tasks mid-flight");
        builder.AppendLine("## Pending User Asks -- questions waiting for user response");
        builder.AppendLine("## Remaining Work -- planned but not started");
        builder.AppendLine();
        builder.AppendLine($"Keep the summary under {maxChars} characters.");

        if (!string.IsNullOrWhiteSpace(priorSummary))
        {
            builder.AppendLine();
            builder.AppendLine("The prior compaction summary is provided below for iterative context merge.");
            builder.AppendLine("Merge the prior summary with the new turns into a single updated summary.");
            builder.AppendLine();
            builder.AppendLine("## Prior Summary");
            builder.AppendLine(priorSummary);
        }

        builder.AppendLine();
        builder.AppendLine("Conversation:");

        foreach (var entry in entries)
        {
            var content = TruncateForSummarization(entry);
            builder.AppendLine($"[{entry.Role}]: {content}");
        }

        // Guard: if total prompt exceeds max chars, drop oldest entries until it fits.
        var result = builder.ToString();
        if (result.Length > MaxSummarizationPromptChars)
        {
            builder.Clear();
            builder.AppendLine("Summarize the following conversation history. Preserve critical information in a structured format.");
            builder.AppendLine();
            builder.AppendLine("Required sections:");
            builder.AppendLine("## Resolved -- completed tasks, decisions made");
            builder.AppendLine("## Active Task -- what was being worked on at compaction time");
            builder.AppendLine("## In Progress -- tool calls / sub-tasks mid-flight");
            builder.AppendLine("## Pending User Asks -- questions waiting for user response");
            builder.AppendLine("## Remaining Work -- planned but not started");
            builder.AppendLine();
            builder.AppendLine($"Keep the summary under {maxChars} characters.");
            builder.AppendLine();
            builder.AppendLine("NOTE: This history was truncated to fit the model context window. Focus on the most recent activity.");
            builder.AppendLine();
            builder.AppendLine("Conversation:");

            // Re-build with progressively fewer entries (drop oldest first)
            var remaining = entries;
            while (remaining.Count > 0)
            {
                var candidateBuilder = new StringBuilder(builder.ToString());
                foreach (var entry in remaining)
                {
                    candidateBuilder.AppendLine($"[{entry.Role}]: {TruncateForSummarization(entry)}");
                }

                if (candidateBuilder.Length <= MaxSummarizationPromptChars)
                {
                    return candidateBuilder.ToString();
                }

                // Drop the oldest quarter of entries
                var dropCount = Math.Max(1, remaining.Count / 4);
                remaining = remaining.Skip(dropCount).ToList();
            }

            // Absolute fallback: just the prompt header
            return builder.ToString();
        }

        return result;
    }

    /// <summary>
    /// Truncates a session entry's content for inclusion in the summarization prompt.
    /// Tool entries are aggressively truncated since their full output is rarely
    /// needed for a high-level summary.
    /// </summary>
    internal static string TruncateForSummarization(SessionEntry entry)
    {
        var content = entry.Content ?? string.Empty;
        if (content.Length <= MaxEntryContentCharsInPrompt)
            return content;

        // For tool entries, keep even less — just first 200 chars
        var limit = entry.Role.Equals(MessageRole.Tool)
            ? Math.Min(200, MaxEntryContentCharsInPrompt)
            : MaxEntryContentCharsInPrompt;

        return content[..limit] + $"... [truncated, {content.Length} chars total]";
    }

    private async Task<string> CallLlmForSummaryAsync(
        string summaryPrompt,
        CompactionOptions options,
        CancellationToken cancellationToken)
    {
        var candidates = BuildCandidateModels(options.SummarizationModel, options.SummarizationProvider);

        var context = new Context(
            SystemPrompt: null,
            Messages:
            [
                new UserMessage(new UserMessageContent(summaryPrompt), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            ]);

        for (var i = 0; i < candidates.Count; i++)
        {
            var model = candidates[i];

            // Caller cancellation (not a per-attempt timeout) ends the whole chain.
            cancellationToken.ThrowIfCancellationRequested();

            // Bug 3: log the resolved model so failures are diagnosable.
            _logger.LogDebug(
                "Requesting compaction summary via model {ModelId} (provider {Provider}) [candidate {Index}/{Total}]",
                model.Id, model.Provider, i + 1, candidates.Count);

            var (result, transientFailure) =
                await TryCallModelAsync(model, context, options, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(result))
            {
                if (i > 0)
                {
                    _logger.LogInformation(
                        "Compaction summary succeeded on fallback model {ModelId} (candidate {Index}/{Total}).",
                        model.Id, i + 1, candidates.Count);
                }
                return result;
            }

            // Empty/failed result. If more candidates remain, fall through to the next model so a
            // single model's transient outage (e.g. an HTTP 421 burst) does not abort compaction.
            if (i < candidates.Count - 1)
            {
                _logger.LogWarning(
                    "Compaction summary model {ModelId} returned no usable result ({Reason}). " +
                    "Falling back to next candidate model.",
                    model.Id, transientFailure ? "transient failure" : "empty response");
            }
        }

        // All candidates exhausted with no usable summary. Returning empty lets the caller abort
        // without mutating history (and increments the circuit breaker).
        return string.Empty;
    }

    /// <summary>
    /// Attempts a single summarization call against one model. Returns the trimmed summary text (or
    /// empty if none) and whether the attempt failed transiently (timeout / provider error). A
    /// per-attempt timeout is treated as a transient failure of <em>this</em> model, not a caller
    /// cancellation, so the surrounding fallback loop can try the next candidate.
    /// </summary>
    private async Task<(string Result, bool TransientFailure)> TryCallModelAsync(
        LlmModel model,
        Context context,
        CompactionOptions options,
        CancellationToken cancellationToken)
    {
        // Resolve API key from GatewayAuthManager (OAuth token from auth.json).
        // Without this, the provider falls back to environment variables which
        // are not set in the gateway process — resulting in auth failures that
        // surface as empty content responses.
        string? apiKey = null;
        if (_authManager is not null)
        {
            apiKey = await _authManager.GetApiKeyAsync(model.Provider, cancellationToken)
                .ConfigureAwait(false);
        }

        var streamOptions = apiKey is not null
            ? new SimpleStreamOptions { ApiKey = apiKey, CancellationToken = cancellationToken }
            : null;

        // Create a timeout-linked token so hung provider calls are cancelled after
        // CompactionOptions.TimeoutSeconds. The linked token fires on whichever
        // triggers first: the caller's cancellation or the configured timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        AssistantMessage completion;
        try
        {
            completion = await _llmClient
                .CompleteSimpleAsync(model, context, streamOptions)
                .WaitAsync(timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Per-attempt timeout fired (not caller cancellation). Treat as a transient failure of
            // this model so the fallback loop can try the next candidate.
            _logger.LogWarning(
                "Compaction summary model {ModelId} timed out after {Timeout}s.",
                model.Id, options.TimeoutSeconds);
            return (string.Empty, true);
        }

        // Bug 3: log what actually came back before filtering.
        _logger.LogDebug(
            "Compaction LLM response: {ContentItemCount} content item(s), TextContent items: {TextCount}",
            completion.Content.Count,
            completion.Content.OfType<TextContent>().Count());

        var result = string.Join(
            Environment.NewLine,
            completion.Content
                .OfType<TextContent>()
                .Select(content => content.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

        if (string.IsNullOrWhiteSpace(result))
        {
            _logger.LogWarning(
                "Model {ModelId} returned no usable TextContent for compaction summary. " +
                "Raw content types: {Types}. StopReason: {StopReason}. ErrorMessage: {ErrorMessage}",
                model.Id,
                string.Join(", ", completion.Content.Select(c => c.GetType().Name)),
                completion.StopReason,
                completion.ErrorMessage ?? "(none)");
            // An error StopReason (e.g. the HTTP 421 the provider surfaced) is a transient failure;
            // a clean-but-empty response is not, but both are treated the same for fallback purposes.
            var transient = completion.StopReason == StopReason.Error;
            return (string.Empty, transient);
        }

        return (result, false);
    }

    /// <summary>
    /// Builds the ordered, de-duplicated list of candidate models to try for summarization.
    /// The primary (explicitly requested or session) model is tried first; if it fails transiently,
    /// the cheaper default summary models are tried in turn. This means one model's transient
    /// routing/outage problem cannot wedge a session — a different model can still produce the
    /// summary that lets the session shed context.
    /// </summary>
    internal IReadOnlyList<LlmModel> BuildCandidateModels(string? requestedModelId, string? preferredProvider)
    {
        var candidates = new List<LlmModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(LlmModel? model)
        {
            if (model is null) return;
            // De-dupe on provider+id so we don't retry the identical endpoint.
            var key = $"{model.Provider}::{model.Id}";
            if (seen.Add(key))
                candidates.Add(model);
        }

        // 1. Primary: the explicitly requested/aux model (throws if a requested model is unregistered,
        //    preserving existing behaviour), otherwise the default waterfall's first hit.
        Add(ResolveModel(requestedModelId, preferredProvider));

        // 2. Fallbacks: the remaining default summary models (cheap, broadly available), in order.
        foreach (var modelId in DefaultSummaryModelIds)
        {
            if (!string.IsNullOrWhiteSpace(preferredProvider))
                Add(_llmClient.Models.GetModel(preferredProvider, modelId));
            Add(FindModel(modelId));
        }

        return candidates;
    }

    private LlmModel ResolveModel(string? requestedModelId, string? preferredProvider = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedModelId))
        {
            // If provider specified, look there first
            if (!string.IsNullOrWhiteSpace(preferredProvider))
            {
                var providerMatch = _llmClient.Models.GetModel(preferredProvider, requestedModelId);
                if (providerMatch is not null)
                    return providerMatch;
            }

            var exact = FindModel(requestedModelId);
            if (exact is not null)
            {
                return exact;
            }

            throw new InvalidOperationException($"Summarization model '{requestedModelId}' is not registered.");
        }

        foreach (var modelId in DefaultSummaryModelIds)
        {
            // Prefer the configured provider
            if (!string.IsNullOrWhiteSpace(preferredProvider))
            {
                var providerMatch = _llmClient.Models.GetModel(preferredProvider, modelId);
                if (providerMatch is not null)
                    return providerMatch;
            }

            var preferred = FindModel(modelId);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        var fallback = _llmClient.Models
            .GetProviders()
            .OrderBy(provider => provider, StringComparer.Ordinal)
            .SelectMany(provider => _llmClient.Models.GetModels(provider))
            .FirstOrDefault();

        return fallback
               ?? throw new InvalidOperationException("No models are registered for session compaction.");
    }

    private LlmModel? FindModel(string modelId)
    {
        foreach (var provider in _llmClient.Models.GetProviders())
        {
            var model = _llmClient.Models.GetModels(provider)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase));

            if (model is not null)
            {
                return model;
            }
        }

        return null;
    }

    private static int EstimateVisibleTokenCount(Session session)
    {
        var totalChars = session.History
            .Where(SessionContextProjector.IsVisibleInLiveContext)
            .Sum(entry => (long)(entry.Content?.Length ?? 0));
        return (int)Math.Min(totalChars / 4, int.MaxValue);
    }

    private static int EstimateVisibleTokenCountFromEntries(IEnumerable<SessionEntry> entries)
    {
        var totalChars = entries
            .Where(SessionContextProjector.IsVisibleInLiveContext)
            .Sum(entry => (long)(entry.Content?.Length ?? 0));
        return (int)Math.Min(totalChars / 4, int.MaxValue);
    }

    /// <summary>
    /// Extracts the raw LLM summary text from the most recent compaction summary entry in the
    /// entries-to-summarise list. The guardrail prefix is stripped so it is not re-processed
    /// as instructions in the next cycle's prompt.
    /// Returns null when no prior summary exists (first compaction cycle).
    /// </summary>
    private static string? ExtractPriorSummary(IReadOnlyList<SessionEntry> entriesToSummarize)
    {
        var summaryEntry = entriesToSummarize
            .LastOrDefault(e => e.IsCompactionSummary);
        if (summaryEntry is null) return null;

        var content = summaryEntry.Content ?? string.Empty;
        // Strip the guardrail prefix that was prepended when the entry was stored.
        if (content.StartsWith(SummaryPrefix, StringComparison.Ordinal))
            content = content[SummaryPrefix.Length..].TrimStart('\n');
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    /// <summary>
    /// Builds an effective <see cref="CompactionOptions"/> by substituting the
    /// <c>auxiliary.compression</c> model when the caller did not specify an explicit
    /// <see cref="CompactionOptions.SummarizationModel"/>.
    /// If no aux model is configured the options are returned unchanged (the existing
    /// default waterfall in <see cref="ResolveModel"/> continues to apply).
    /// Emits a startup-visible warning when no aux model is configured and no explicit
    /// model was requested so operators know the primary model will be used.
    /// </summary>
    private CompactionOptions ResolveEffectiveOptions(CompactionOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SummarizationModel))
            return options; // explicit override wins

        var compressionModel = _platformConfig?.CurrentValue?.Gateway?.Auxiliary?.Compression;
        if (!string.IsNullOrWhiteSpace(compressionModel))
        {
            _logger.LogDebug(
                "Compaction: using auxiliary.compression model {CompressionModel} for summarisation.",
                compressionModel);
            return options with { SummarizationModel = compressionModel };
        }

        _logger.LogDebug(
            "Compaction: no auxiliary.compression configured -- falling back to primary model waterfall.");
        return options;
    }
}
