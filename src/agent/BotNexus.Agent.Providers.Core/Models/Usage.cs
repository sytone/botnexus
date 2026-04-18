namespace BotNexus.Agent.Providers.Core.Models;

/// <summary>
/// Represents usage cost.
/// </summary>
public record UsageCost(
    decimal Input,
    decimal Output,
    decimal CacheRead,
    decimal CacheWrite,
    decimal Total
);

/// <summary>
/// Represents usage.
/// </summary>
public sealed record Usage
{
    /// <summary>
    /// Gets or sets the input.
    /// </summary>
    public int Input { get; init; }
    /// <summary>
    /// Gets or sets the output.
    /// </summary>
    public int Output { get; init; }
    /// <summary>
    /// Gets or sets the cache read.
    /// </summary>
    public int CacheRead { get; init; }
    /// <summary>
    /// Gets or sets the cache write.
    /// </summary>
    public int CacheWrite { get; init; }
    /// <summary>
    /// Gets or sets the total tokens.
    /// </summary>
    public int TotalTokens { get; init; }
    /// <summary>
    /// Gets or sets the cost.
    /// </summary>
    public UsageCost Cost { get; init; } = new(0, 0, 0, 0, 0);

    /// <summary>
    /// Executes empty.
    /// </summary>
    /// <returns>The empty result.</returns>
    public static Usage Empty() => new();
}
