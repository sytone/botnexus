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
    /// Gets the reasoning tokens the provider reported for this turn, or <see langword="null"/> when
    /// the provider did not report a reasoning breakdown at all (#3297).
    /// </summary>
    /// <remarks>
    /// The null/zero distinction is deliberate and load-bearing: <see langword="null"/> means "not
    /// reported", <c>0</c> means "reported, and it was zero". Coercing absent to zero would present a
    /// missing measurement as a measured one, which is how a thinking-heavy model gets ranked as free.
    /// <para>
    /// This is an attribution field, not a subtraction: <see cref="Output"/> keeps its inclusive
    /// meaning (reasoning tokens are already counted in it) so every existing consumer and
    /// <c>ModelRegistry.CalculateCost</c> are unaffected.
    /// </para>
    /// </remarks>
    public int? Reasoning { get; init; }
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
