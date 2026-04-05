using BotNexus.Providers.Core.Models;

namespace BotNexus.Providers.Core.Utilities;

/// <summary>
/// Build StreamOptions from SimpleStreamOptions, calculate thinking budgets.
/// Port of pi-mono's providers/simple-options.ts.
/// </summary>
public static class SimpleOptionsHelper
{
    private static readonly IReadOnlyDictionary<ThinkingLevel, int> DefaultThinkingBudgets = new Dictionary<ThinkingLevel, int>
    {
        [ThinkingLevel.Minimal] = 1024,
        [ThinkingLevel.Low] = 2048,
        [ThinkingLevel.Medium] = 8192,
        [ThinkingLevel.High] = 16384,
        [ThinkingLevel.ExtraHigh] = 16384
    };

    /// <summary>
    /// Build base StreamOptions from SimpleStreamOptions, resolving API key.
    /// </summary>
    public static StreamOptions BuildBaseOptions(LlmModel model, SimpleStreamOptions? options, string apiKey)
    {
        return new StreamOptions
        {
            Temperature = options?.Temperature,
            MaxTokens = options?.MaxTokens,
            CancellationToken = options?.CancellationToken ?? CancellationToken.None,
            ApiKey = apiKey,
            Transport = options?.Transport ?? Transport.Sse,
            CacheRetention = options?.CacheRetention ?? CacheRetention.Short,
            SessionId = options?.SessionId,
            OnPayload = options?.OnPayload,
            Headers = options?.Headers,
            MaxRetryDelayMs = options?.MaxRetryDelayMs ?? 60000,
            Metadata = options?.Metadata,
        };
    }

    /// <summary>
    /// Clamp reasoning level to High for models that don't support ExtraHigh.
    /// </summary>
    public static ThinkingLevel? ClampReasoning(ThinkingLevel? level)
    {
        if (level == ThinkingLevel.ExtraHigh)
            return ThinkingLevel.High;
        return level;
    }

    /// <summary>
    /// Resolve thinking budget for a given level from custom budgets or defaults.
    /// </summary>
    public static ThinkingBudgetLevel? GetBudgetForLevel(
        ThinkingLevel level,
        ThinkingBudgets? customBudgets)
    {
        if (customBudgets is null)
            return null;

        return level switch
        {
            ThinkingLevel.Minimal => customBudgets.Minimal,
            ThinkingLevel.Low => customBudgets.Low,
            ThinkingLevel.Medium => customBudgets.Medium,
            ThinkingLevel.High => customBudgets.High,
            ThinkingLevel.ExtraHigh => customBudgets.ExtraHigh,
            _ => null
        };
    }

    public static int GetDefaultThinkingBudget(ThinkingLevel level)
    {
        if (DefaultThinkingBudgets.TryGetValue(level, out var budget))
            return budget;

        return DefaultThinkingBudgets[ThinkingLevel.Medium];
    }

    /// <summary>
    /// Adjust maxTokens for Anthropic thinking mode.
    /// When thinking is enabled, maxTokens must be large enough to accommodate
    /// both the thinking budget and the output tokens.
    /// </summary>
    public static (int MaxTokens, int ThinkingBudget) AdjustMaxTokensForThinking(
        LlmModel model,
        int? requestedMaxTokens,
        int thinkingBudget)
    {
        var maxTokens = requestedMaxTokens ?? model.MaxTokens;

        // Ensure maxTokens is at least thinkingBudget + minimum output room
        var minRequired = thinkingBudget + 1024;
        if (maxTokens < minRequired)
            maxTokens = minRequired;

        // Cap at model maximum
        if (maxTokens > model.MaxTokens)
            maxTokens = model.MaxTokens;

        // Recalculate budget if it exceeds adjusted maxTokens
        if (thinkingBudget >= maxTokens)
            thinkingBudget = maxTokens - 1024;

        if (thinkingBudget < 1024)
            thinkingBudget = 1024;

        return (maxTokens, thinkingBudget);
    }
}
