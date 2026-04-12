namespace BotNexus.Providers.Core.Models;

/// <summary>
/// Custom token budgets for each thinking level (token-based providers only).
/// </summary>
public record ThinkingBudgets
{
    /// <summary>
    /// Gets or sets the minimal.
    /// </summary>
    public int? Minimal { get; init; }
    /// <summary>
    /// Gets or sets the low.
    /// </summary>
    public int? Low { get; init; }
    /// <summary>
    /// Gets or sets the medium.
    /// </summary>
    public int? Medium { get; init; }
    /// <summary>
    /// Gets or sets the high.
    /// </summary>
    public int? High { get; init; }
    /// <summary>
    /// Gets or sets the extra high.
    /// </summary>
    public int? ExtraHigh { get; init; }
}
