using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core;

/// <summary>Semantic controls owned by an agent generation request, independent of provider transport.</summary>
public record class GenerationOptions
{
    public GenerationOptions() { }

    protected GenerationOptions(GenerationOptions original)
    {
        Temperature = original.Temperature;
        MaxTokens = original.MaxTokens;
        ContextWindow = original.ContextWindow;
        CancellationToken = original.CancellationToken;
        CacheRetention = original.CacheRetention;
        SessionId = original.SessionId;
        Reasoning = original.Reasoning;
        ThinkingBudgets = original.ThinkingBudgets;
    }
    /// <summary>Sampling temperature requested for the generation.</summary>
    public float? Temperature { get; init; }
    /// <summary>Maximum output-token count requested for the generation.</summary>
    public int? MaxTokens { get; init; }
    /// <summary>Selected context-window size in tokens, when supported.</summary>
    public int? ContextWindow { get; init; }
    /// <summary>Cancellation for the logical generation.</summary>
    public CancellationToken CancellationToken { get; init; }
    /// <summary>Prompt-cache retention intent.</summary>
    public CacheRetention CacheRetention { get; init; } = CacheRetention.Short;
    /// <summary>Correlation identifier for the logical session.</summary>
    public string? SessionId { get; init; }
    /// <summary>Requested reasoning level.</summary>
    public ThinkingLevel? Reasoning { get; init; }
    /// <summary>Optional token budgets for reasoning levels.</summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }
}
