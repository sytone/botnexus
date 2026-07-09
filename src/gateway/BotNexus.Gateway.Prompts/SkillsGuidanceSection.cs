namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Provides the built-in skills-guidance prompt section that instructs agents to
/// treat skills as mandatory carriers of project conventions: any partially-relevant
/// skill must be loaded before acting, stale skills must be patched, and reusable
/// procedures should be captured (preferring to extend an umbrella skill over
/// creating narrow one-off skills).
/// </summary>
public static class SkillsGuidanceSection
{
    /// <summary>
    /// The stable section identifier used for override resolution.
    /// </summary>
    public const string Id = "skills-guidance";

    /// <summary>
    /// The XML tag name for this section in the assembled prompt.
    /// </summary>
    public const string Tag = "skills";

    /// <summary>
    /// The ordering position for this section within the prompt pipeline.
    /// Placed later in the pipeline (55) after content/tool sections to serve as
    /// behavioral guidance once the agent knows what tools and skills are available.
    /// </summary>
    public const int SectionOrder = 55;

    private static readonly string[] Lines =
    [
        "Before replying, scan the available skills. If a skill is even partially relevant to the task, it MUST be loaded before you act - do not defer it.",
        "Err on the side of loading: skills carry project conventions, API details, exact tool commands, and proven workflows you cannot reconstruct reliably from general knowledge.",
        "Do not improvise from general knowledge when a relevant skill exists. Load it and follow it.",
        "If a loaded skill is stale, wrong, or missing a pitfall you discover while working, patch it with `skill_manage` before you finish.",
        "After a difficult or iterative task, consider saving the reusable approach as a skill so future work is faster.",
        "Prefer patching an existing umbrella skill over creating a narrow one-off skill. Create new skills only for reusable classes of work, never for one-off task outcomes.",
        "Skill names must be lowercase alphanumeric with hyphens (e.g. `deploy-staging`, `review-pr`)."
    ];

    /// <summary>
    /// Creates a <see cref="LambdaPromptSection"/> for skills-guidance.
    /// The section is only included when the agent has skill-related tools available.
    /// </summary>
    public static LambdaPromptSection Create() =>
        new(SectionOrder, static _ => Lines, sectionId: Id, shouldIncludeFunc: HasSkillTools, xmlTag: Tag);

    private static bool HasSkillTools(PromptContext context) =>
        context.AvailableTools.Contains("skills") || context.AvailableTools.Contains("skill_manage");
}
