using System.Text;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain.Primitives;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.Logging;

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

    public LlmSessionCompactor(LlmClient llmClient, ILogger<LlmSessionCompactor> logger, ISecretRedactor? redactor = null)
    {
        _llmClient = llmClient;
        _logger = logger;
        _redactor = redactor;
    }

    public bool ShouldCompact(Session session, CompactionOptions options)
    {
        var estimatedTokens = EstimateTokenCount(session);
        var threshold = (int)(options.ContextWindowTokens * options.TokenThresholdRatio);
        return estimatedTokens > threshold;
    }

    public async Task<CompactionResult> CompactAsync(
        Session session,
        CompactionOptions options,
        CancellationToken cancellationToken = default)
    {
        var history = session.History;
        if (history.Count == 0)
        {
            return new CompactionResult
            {
                Summary = string.Empty,
                Succeeded = false,
                EntriesSummarized = 0,
                EntriesPreserved = 0,
                TokensBefore = 0,
                TokensAfter = 0
            };
        }

        var (toSummarize, toPreserve) = SplitHistory(history, options.PreservedTurns);
        if (toSummarize.Count == 0)
        {
            return new CompactionResult
            {
                Summary = string.Empty,
                Succeeded = false,
                EntriesSummarized = 0,
                EntriesPreserved = toPreserve.Count,
                TokensBefore = EstimateTokenCount(session),
                TokensAfter = EstimateTokenCount(session)
            };
        }

        var tokensBefore = EstimateTokenCount(session);
        var summaryPrompt = BuildSummarizationPrompt(toSummarize, options.MaxSummaryChars);
        var summary = await CallLlmForSummaryAsync(summaryPrompt, options, cancellationToken).ConfigureAwait(false);

        // Bug 1 / Bug 5 guard: if the LLM returned nothing, abort — do NOT mutate history.
        if (string.IsNullOrWhiteSpace(summary))
        {
            _logger.LogWarning(
                "Compaction aborted for session {SessionId}: LLM returned an empty summary. " +
                "History is unchanged. Summarized {Count} entries would have been deleted.",
                session.SessionId,
                toSummarize.Count);

            return new CompactionResult
            {
                Summary = string.Empty,
                Succeeded = false,
                EntriesSummarized = 0,
                EntriesPreserved = history.Count,
                TokensBefore = tokensBefore,
                TokensAfter = tokensBefore
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
            Content = summary,
            IsCompactionSummary = true,
            Timestamp = DateTimeOffset.UtcNow
        };

        // Bug 4: do NOT assign session.History directly — return the new list so the caller
        // can apply it via GatewaySession.ReplaceHistory() which routes through the runtime lock.
        var newHistory = new List<SessionEntry> { compactionEntry };
        newHistory.AddRange(toPreserve);

        var tokensAfter = EstimateTokenCountFromEntries(newHistory);

        _logger.LogInformation(
            "Compacted session {SessionId}: {Summarized} entries summarized, {Preserved} preserved, " +
            "tokens {Before}→{After} (delta {Delta})",
            session.SessionId,
            toSummarize.Count,
            toPreserve.Count,
            tokensBefore,
            tokensAfter,
            tokensBefore - tokensAfter);

        return new CompactionResult
        {
            Summary = summary,
            Succeeded = true,
            CompactedHistory = newHistory,
            EntriesSummarized = toSummarize.Count,
            EntriesPreserved = toPreserve.Count,
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter
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

    private static (string systemPrompt, string userMessage) BuildSummarizationPrompt(List<SessionEntry> entries, int maxChars)
    {
        var systemPrompt =
            "You are a concise conversation summarizer. When asked, produce a structured summary of the provided conversation. " +
            "Required sections: ## Decisions, ## Open TODOs, ## Constraints, ## Key Identifiers. " +
            $"Keep the summary under {maxChars} characters. Output only the summary — no preamble, no markdown fences.";

        var historyBuilder = new StringBuilder();
        historyBuilder.AppendLine("Summarize the following conversation history:");
        historyBuilder.AppendLine();
        foreach (var entry in entries)
        {
            historyBuilder.AppendLine($"[{entry.Role}]: {entry.Content}");
        }

        return (systemPrompt, historyBuilder.ToString());
    }

    private async Task<string> CallLlmForSummaryAsync(
        (string systemPrompt, string userMessage) prompt,
        CompactionOptions options,
        CancellationToken cancellationToken)
    {
        var model = ResolveModel(options.SummarizationModel, options.SummarizationProvider);
        var result = await TryGetSummaryAsync(model, prompt, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(result))
            return result;

        // Primary model returned empty — attempt fallback auto-selection
        _logger.LogWarning(
            "Model {ModelId} returned no usable content for compaction summary. Attempting fallback model.",
            model.Id);

        var fallback = ResolveFallbackModel(model);
        if (fallback is null)
        {
            _logger.LogWarning(
                "No fallback model available. Compaction aborted for session.");
            return string.Empty;
        }

        _logger.LogInformation(
            "Retrying compaction summary with fallback model {ModelId} (provider {Provider})",
            fallback.Id,
            fallback.Provider);

        result = await TryGetSummaryAsync(fallback, prompt, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<string> TryGetSummaryAsync(
        LlmModel model,
        (string systemPrompt, string userMessage) prompt,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Requesting compaction summary via model {ModelId} (provider {Provider})",
            model.Id,
            model.Provider);

        var context = new Context(
            SystemPrompt: prompt.systemPrompt,
            Messages:
            [
                new UserMessage(new UserMessageContent(prompt.userMessage), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            ]);

        var completion = await _llmClient
            .CompleteSimpleAsync(model, context)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Compaction LLM response from {ModelId}: {ContentItemCount} item(s), TextContent: {TextCount}, types: {Types}",
            model.Id,
            completion.Content.Count,
            completion.Content.OfType<TextContent>().Count(),
            string.Join(", ", completion.Content.Select(c => c.GetType().Name)));

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

    /// <summary>
    /// Resolves a fallback model when the primary returns empty content.
    /// Excludes the primary model and prefers the first available from the default list.
    /// </summary>
    private LlmModel? ResolveFallbackModel(LlmModel primary)
    {
        foreach (var modelId in DefaultSummaryModelIds)
        {
            if (string.Equals(modelId, primary.Id, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = FindModel(modelId);
            if (candidate is not null)
                return candidate;
        }

        // Last resort: any registered model that isn't the primary
        return _llmClient.Models
            .GetProviders()
            .OrderBy(p => p, StringComparer.Ordinal)
            .SelectMany(p => _llmClient.Models.GetModels(p))
            .FirstOrDefault(m => !string.Equals(m.Id, primary.Id, StringComparison.OrdinalIgnoreCase));
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

    private static int EstimateTokenCount(Session session)
    {
        var totalChars = session.History.Sum(entry => (long)(entry.Content?.Length ?? 0));
        return (int)Math.Min(totalChars / 4, int.MaxValue);
    }

    private static int EstimateTokenCountFromEntries(IEnumerable<SessionEntry> entries)
    {
        var totalChars = entries.Sum(entry => (long)(entry.Content?.Length ?? 0));
        return (int)Math.Min(totalChars / 4, int.MaxValue);
    }
}
