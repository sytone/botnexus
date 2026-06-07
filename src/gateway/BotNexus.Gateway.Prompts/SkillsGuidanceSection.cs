namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Provides the built-in skills-guidance prompt section that instructs agents to
/// proactively load skills for domain-specific knowledge and create new skills
/// to capture reusable procedures.
/// </summary>
public static class SkillsGuidanceSection
{
    /// <summary>
    /// The stable section identifier used for override resolution.
    /// </summary>
    public const string Id = "skills-guidance";

    /// <summary>
    /// The ordering position for this section within the prompt pipeline.
    /// Placed later in the pipeline (55) after content/tool sections to serve as
    /// behavioral guidance once the agent knows what tools and skills are available.
    /// </summary>
    public const int SectionOrder = 55;

    private static readonly string[] Lines =
    [
        "## Skills",
        "Before starting domain-specific work, check available skills with `skills list` and load relevant ones.",
        "Skills contain reusable procedures, references, and templates — always load before improvising.",
        "When you discover a repeatable multi-step procedure, create a skill to capture it for future use.",
        "Skill names must be lowercase alphanumeric with hyphens (e.g. `deploy-staging`, `review-pr`).",
        "Keep skill content focused: one skill per procedure, with clear steps and examples."
    ];

    /// <summary>
    /// Creates a <see cref="LambdaPromptSection"/> for skills-guidance.
    /// The section is only included when the agent has skill-related tools available.
    /// </summary>
    public static LambdaPromptSection Create() =>
        new(SectionOrder, static _ => Lines, sectionId: Id, shouldIncludeFunc: HasSkillTools);

    private static bool HasSkillTools(PromptContext context) =>
        context.AvailableTools.Contains("skills") || context.AvailableTools.Contains("skill_manage");
}
