using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Utilities;

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
            MaxTokens = options?.MaxTokens ?? Math.Min(model.MaxTokens, 32000),
            CancellationToken = options?.CancellationToken ?? CancellationToken.None,
            ApiKey = string.IsNullOrEmpty(apiKey) ? options?.ApiKey : apiKey,
            Transport = options?.Transport ?? new StreamOptions().Transport,
            CacheRetention = options?.CacheRetention ?? new StreamOptions().CacheRetention,
            SessionId = options?.SessionId,
            OnPayload = options?.OnPayload,
            Headers = options?.Headers,
            MaxRetryDelayMs = options?.MaxRetryDelayMs ?? new StreamOptions().MaxRetryDelayMs,
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
    public static int? GetBudgetForLevel(
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

    /// <summary>
    /// Executes get default thinking budget.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <returns>The get default thinking budget result.</returns>
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
        var baseMaxTokens = requestedMaxTokens ?? model.MaxTokens;
        var maxTokens = Math.Min(baseMaxTokens + thinkingBudget, model.MaxTokens);

        if (maxTokens <= thinkingBudget)
            thinkingBudget = Math.Max(0, maxTokens - 1024);

        return (maxTokens, thinkingBudget);
    }
}
