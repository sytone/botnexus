namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Separates the selected working budget from the registered model's capacity declarations.
/// Origins describe configuration or registration, never provider verification. This snapshot
/// describes a handle's binding; it does not reserve output tokens or change compaction ratios.
/// </summary>
public sealed record ContextBudgetDiagnostics
{
    /// <summary>The usable model context declaration, or null when unknown.</summary>
    public int? ModelContextWindowTokens { get; init; }

    /// <summary>Origin of the usable context declaration, not evidence of a provider hard limit.</summary>
    public string? ModelContextWindowSource { get; init; }

    /// <summary>The usable model output declaration, independent of the working context budget.</summary>
    public int? ModelMaxOutputTokens { get; init; }

    /// <summary>Origin of the usable output declaration, not evidence of provider verification.</summary>
    public string? ModelMaxOutputSource { get; init; }

    /// <summary>The selected override or model context window; null leaves global compaction options unchanged.</summary>
    public int? EffectiveWorkingBudgetTokens { get; init; }

    /// <summary>Selection layer (conversation, agent, or model), or null when the origin is unknown.</summary>
    public string? EffectiveWorkingBudgetSource { get; init; }
}
