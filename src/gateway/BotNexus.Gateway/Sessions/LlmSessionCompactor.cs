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

    /// <summary>
    /// Tracks consecutive compaction failures per session for circuit breaker logic.
    /// After <see cref="MaxConsecutiveFailures"/> consecutive failures, compaction is
    /// skipped for that session until the gateway restarts.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();

    /// <summary>
    /// Maximum consecutive compaction failures before the circuit breaker opens.
    /// </summary>
    internal const int MaxConsecutiveFailures = 3;

    public LlmSessionCompactor(LlmClient llmClient, ILogger<LlmSessionCompactor> logger, ISecretRedactor? redactor = null, IOptionsMonitor<PlatformConfig>? platformConfig = null)
    {
        _llmClient = llmClient;
        _logger = logger;
        _redactor = redactor;
        _platformConfig = platformConfig;
    }

    public bool ShouldCompact(Session session, CompactionOptions options)
    {
        var estimatedTokens = EstimateVisibleTokenCount(session);
        var threshold = (int)(options.ContextWindowTokens * options.TokenThresholdRatio);
        return estimatedTokens > threshold;
    }

    public async Task<CompactionResult> CompactAsync(
        GatewaySession session,
        CompactionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Circuit breaker: skip compaction if this session has failed too many times.
        var sessionKey = session.SessionId.Value;
        var failures = _consecutiveFailures.GetValueOrDefault(sessionKey, 0);
        if (failures >= MaxConsecutiveFailures)
        {
            _logger.LogWarning(
                "Compaction circuit breaker OPEN for session {SessionId}: " +
                "{Failures} consecutive failures. Skipping until gateway restart.",
                sessionKey, failures);
            return new CompactionResult
            {
                Summary = string.Empty,
                Succeeded = false,
                EntriesSummarized = 0,
                EntriesPreserved = 0,
                TokensBefore = 0,
                TokensAfter = 0,
                SnapshotDestructiveVersion = 0,
                SnapshotHistoryCount = 0
            };
        }

        // Atomic snapshot: history copy + destructive-mutation version + count, all
        // captured under the runtime lock. The compactor operates only on this
        // immutable snapshot below; live `session.History` is not read again until
        // the caller applies the result via TryReplaceHistoryFromSnapshot (#532).
        var snap = session.SnapshotHistoryForCompaction();
        var history = snap.Entries;
        if (history.Count == 0)
        {
            return new CompactionResult
            {
                Summary = string.Empty,
                Succeeded = false,
                EntriesSummarized = 0,
                EntriesPreserved = 0,
                TokensBefore = 0,
                TokensAfter = 0,
                SnapshotDestructiveVersion = snap.DestructiveVersion,
                SnapshotHistoryCount = snap.Count
            };
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
            return new CompactionResult
            {
                Summary = string.Empty,
                Succeeded = false,
                EntriesSummarized = 0,
                EntriesPreserved = toPreserve.Count,
                TokensBefore = visibleTokens,
                TokensAfter = visibleTokens,
                SnapshotDestructiveVersion = snap.DestructiveVersion,
                SnapshotHistoryCount = snap.Count
            };
        }

        var tokensBefore = EstimateVisibleTokenCountFromEntries(history);
        var priorSummary = ExtractPriorSummary(toSummarize);
        var summaryPrompt = BuildSummarizationPrompt(toSummarize, options.MaxSummaryChars, priorSummary);
        var effectiveOptions = ResolveEffectiveOptions(options);
        var summary = await CallLlmForSummaryAsync(summaryPrompt, effectiveOptions, cancellationToken).ConfigureAwait(false);

        // Bug 1 / Bug 5 guard: if the LLM returned nothing, abort — do NOT mutate history.
        if (string.IsNullOrWhiteSpace(summary))
        {
            _consecutiveFailures.AddOrUpdate(sessionKey, 1, (_, count) => count + 1);
            _logger.LogWarning(
                "Compaction aborted for session {SessionId}: LLM returned an empty summary. " +
                "History is unchanged. Summarized {Count} entries would have been marked as historical. " +
                "Consecutive failures: {Failures}/{Max}",
                session.SessionId,
                toSummarize.Count,
                _consecutiveFailures.GetValueOrDefault(sessionKey, 1),
                MaxConsecutiveFailures);

            return new CompactionResult
            {
                Summary = string.Empty,
                Succeeded = false,
                EntriesSummarized = 0,
                EntriesPreserved = history.Count,
                TokensBefore = tokensBefore,
                TokensAfter = tokensBefore,
                SnapshotDestructiveVersion = snap.DestructiveVersion,
                SnapshotHistoryCount = snap.Count
            };
        }

        if (summary.Length > options.MaxSummaryChars)
        {
            summary = summary[..options.MaxSummaryChars];
        }

        // Redact any secrets that leaked into the LLM summary before persisting.
        if (_redactor is not null)
            summary = _redactor.Redact(summary);

        var compactionEntry = new SessionEntry
        {
            Role = MessageRole.System,
            Content = SummaryPrefix + "\n" + summary,
            IsCompactionSummary = true,
            Timestamp = DateTimeOffset.UtcNow
        };

        // Phase 3a: build the new history by walking the ORIGINAL history. Entries in toSummarize
        // are marked IsHistory=true (folded); all other entries — pre-existing historical entries
        // from prior compactions, crash sentinels, and the preserved tail — pass through verbatim.
        // The new summary is inserted at the index immediately AFTER the last toSummarize entry's
        // original position so chronological order is preserved for transcript readers.
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
        _consecutiveFailures.TryRemove(sessionKey, out _);

        return new CompactionResult
        {
            Summary = summary,
            Succeeded = true,
            CompactedHistory = newHistory,
            EntriesSummarized = toSummarize.Count,
            EntriesPreserved = toPreserve.Count,
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter,
            SnapshotDestructiveVersion = snap.DestructiveVersion,
            SnapshotHistoryCount = snap.Count
        };
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
            builder.AppendLine($"[{entry.Role}]: {entry.Content}");
        }

        return builder.ToString();
    }

    private async Task<string> CallLlmForSummaryAsync(
        string summaryPrompt,
        CompactionOptions options,
        CancellationToken cancellationToken)
    {
        var model = ResolveModel(options.SummarizationModel, options.SummarizationProvider);

        // Bug 3: log the resolved model so failures are diagnosable.
        _logger.LogDebug(
            "Requesting compaction summary via model {ModelId} (provider {Provider})",
            model.Id,
            model.Provider);

        var context = new Context(
            SystemPrompt: null,
            Messages:
            [
                new UserMessage(new UserMessageContent(summaryPrompt), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            ]);

        var completion = await _llmClient
            .CompleteSimpleAsync(model, context)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

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
                "Raw content types: {Types}",
                model.Id,
                string.Join(", ", completion.Content.Select(c => c.GetType().Name)));
        }

        return result;
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
