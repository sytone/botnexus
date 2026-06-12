namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Provides the built-in model-guidance prompt section that injects per-model-family
/// behavioral defaults into the system prompt. Detects the model family from the model
/// identifier passed through <see cref="PromptContext.Extensions"/> and emits
/// family-specific rules.
/// </summary>
public static class ModelGuidanceSection
{
    /// <summary>
    /// The stable section identifier used for override resolution.
    /// </summary>
    public const string Id = "model-guidance";

    /// <summary>
    /// The XML tag name for this section in the assembled prompt.
    /// </summary>
    public const string Tag = "model_guidance";

    /// <summary>
    /// The ordering position for this section within the prompt pipeline.
    /// Placed late (135) so model-specific instructions come after all content sections.
    /// </summary>
    public const int SectionOrder = 135;

    /// <summary>
    /// The <see cref="PromptContext.Extensions"/> key used to pass the model identifier
    /// through to the section builder.
    /// </summary>
    public const string ModelIdExtensionKey = "modelId";

    private static readonly string[] ClaudeGuidance =
    [
        "Prefer the edit tool over write for modifying existing files — it preserves context and is more precise.",
        "When editing, use the smallest possible oldText/newText to target changes precisely.",
        "You have extended thinking capabilities — use them for complex reasoning before acting."
    ];

    private static readonly string[] GptGuidance =
    [
        "Never answer from memory when a tool can verify the answer — always check the source.",
        "When asked about file contents, always read the file rather than guessing from context.",
        "Be explicit about uncertainty — say when you are unsure rather than confabulating."
    ];

    private static readonly string[] GeminiGuidance =
    [
        "Always use absolute paths in file operations — relative paths may resolve incorrectly.",
        "When referencing files, use the full path from the workspace root.",
        "Verify tool output carefully before proceeding — do not assume success without checking."
    ];

    /// <summary>
    /// Creates a <see cref="LambdaPromptSection"/> for model-guidance.
    /// The section is only included when a recognized model family is detected.
    /// </summary>
    public static LambdaPromptSection Create() =>
        new(SectionOrder, BuildLines, sectionId: Id, shouldIncludeFunc: ShouldInclude, xmlTag: Tag);

    private static bool ShouldInclude(PromptContext context)
    {
        var modelId = context.Get<string>(ModelIdExtensionKey);
        var family = ModelFamilyDetector.GetModelFamily(modelId);
        return family != ModelFamilyDetector.Unknown;
    }

    private static IReadOnlyList<string> BuildLines(PromptContext context)
    {
        var modelId = context.Get<string>(ModelIdExtensionKey);
        var family = ModelFamilyDetector.GetModelFamily(modelId);

        return family switch
        {
            ModelFamilyDetector.Claude => ClaudeGuidance,
            ModelFamilyDetector.Gpt => GptGuidance,
            ModelFamilyDetector.Gemini => GeminiGuidance,
            _ => []
        };
    }
}
