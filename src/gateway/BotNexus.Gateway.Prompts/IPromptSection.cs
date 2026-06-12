namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Defines the contract for a prompt section that contributes lines to the system prompt.
/// </summary>
public interface IPromptSection
{
    /// <summary>
    /// Gets the ordering position of this section within the prompt pipeline.
    /// Lower values appear earlier in the assembled prompt.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Gets the optional stable identifier for this section. Used for override resolution
    /// and diagnostics. Null indicates an anonymous section that cannot be overridden.
    /// </summary>
    string? SectionId => null;

    /// <summary>
    /// Gets the optional XML tag name used to wrap this section's output in the assembled prompt.
    /// When non-null, the pipeline wraps the section output in &lt;tag&gt;...&lt;/tag&gt;.
    /// Null means no wrapping (content emitted raw — used for user workspace files).
    /// </summary>
    string? XmlTag => null;

    /// <summary>
    /// Determines whether this section should be included given the current prompt context.
    /// </summary>
    bool ShouldInclude(PromptContext context);

    /// <summary>
    /// Builds the prompt lines for this section.
    /// </summary>
    IReadOnlyList<string> Build(PromptContext context);
}
