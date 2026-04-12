using System.Text;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain.Primitives;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
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

    public LlmSessionCompactor(LlmClient llmClient, ILogger<LlmSessionCompactor> logger)
    {
        _llmClient = llmClient;
        _logger = logger;
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
                EntriesSummarized = 0,
                EntriesPreserved = toPreserve.Count,
                TokensBefore = EstimateTokenCount(session),
                TokensAfter = EstimateTokenCount(session)
            };
        }

        var tokensBefore = EstimateTokenCount(session);
        var summaryPrompt = BuildSummarizationPrompt(toSummarize, options.MaxSummaryChars);
        var summary = await CallLlmForSummaryAsync(summaryPrompt, options, cancellationToken).ConfigureAwait(false);

        if (summary.Length > options.MaxSummaryChars)
        {
            summary = summary[..options.MaxSummaryChars];
        }

        var compactionEntry = new SessionEntry
        {
            Role = MessageRole.System,
            Content = summary,
            IsCompactionSummary = true,
            Timestamp = DateTimeOffset.UtcNow
        };

        var newHistory = new List<SessionEntry> { compactionEntry };
        newHistory.AddRange(toPreserve);
        session.History = newHistory;
        session.UpdatedAt = DateTimeOffset.UtcNow;

        var tokensAfter = EstimateTokenCount(session);

        _logger.LogInformation(
            "Compacted session {SessionId}: {Summarized} entries summarized, {Preserved} preserved, tokens {Before}→{After}",
            session.SessionId,
            toSummarize.Count,
            toPreserve.Count,
            tokensBefore,
            tokensAfter);

        return new CompactionResult
        {
            Summary = summary,
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

    private static string BuildSummarizationPrompt(List<SessionEntry> entries, int maxChars)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Summarize the following conversation history. Preserve critical information in a structured format.");
        builder.AppendLine();
        builder.AppendLine("Required sections:");
        builder.AppendLine("## Decisions - Key decisions made");
        builder.AppendLine("## Open TODOs - Incomplete tasks and follow-ups");
        builder.AppendLine("## Constraints - Rules or constraints established");
        builder.AppendLine("## Key Identifiers - File paths, UUIDs, URLs, hashes that must be preserved exactly");
        builder.AppendLine();
        builder.AppendLine($"Keep the summary under {maxChars} characters.");
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

        return string.Join(
            Environment.NewLine,
            completion.Content
                .OfType<TextContent>()
                .Select(content => content.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
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
        var totalChars = session.History.Sum(entry => entry.Content?.Length ?? 0);
        return totalChars / 4;
    }
}
