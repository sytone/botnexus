namespace BotNexus.Gateway.Prompts;

/// <summary>
/// A prompt section backed by a delegate that lazily builds its content lines.
/// Supports an optional <see cref="SectionId"/> for override resolution.
/// </summary>
public sealed class LambdaPromptSection : IPromptSection
{
    private readonly Func<PromptContext, IReadOnlyList<string>> _buildFunc;
    private readonly Func<PromptContext, bool>? _shouldIncludeFunc;

    /// <summary>
    /// Creates a new <see cref="LambdaPromptSection"/> with the given order and build delegate.
    /// </summary>
    /// <param name="order">Ordering position within the pipeline.</param>
    /// <param name="buildFunc">Delegate that produces the prompt lines.</param>
    /// <param name="sectionId">Optional stable identifier for override resolution.</param>
    /// <param name="shouldIncludeFunc">Optional predicate controlling inclusion. Defaults to always included.</param>
    public LambdaPromptSection(
        int order,
        Func<PromptContext, IReadOnlyList<string>> buildFunc,
        string? sectionId = null,
        Func<PromptContext, bool>? shouldIncludeFunc = null)
    {
        ArgumentNullException.ThrowIfNull(buildFunc);
        Order = order;
        _buildFunc = buildFunc;
        SectionId = sectionId;
        _shouldIncludeFunc = shouldIncludeFunc;
    }

    /// <inheritdoc/>
    public int Order { get; }

    /// <inheritdoc/>
    public string? SectionId { get; }

    /// <inheritdoc/>
    public bool ShouldInclude(PromptContext context) =>
        _shouldIncludeFunc?.Invoke(context) ?? true;

    /// <inheritdoc/>
    public IReadOnlyList<string> Build(PromptContext context) =>
        _buildFunc(context);
}
