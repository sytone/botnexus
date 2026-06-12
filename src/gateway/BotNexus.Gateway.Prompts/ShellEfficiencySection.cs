namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Provides the built-in shell-efficiency prompt section that guides agents to prefer
/// scripts over repeated individual commands, avoid common shell anti-patterns, and
/// minimize round-trips.
/// </summary>
public static class ShellEfficiencySection
{
    /// <summary>
    /// The stable section identifier used for override resolution.
    /// </summary>
    public const string Id = "shell-efficiency";

    /// <summary>
    /// The XML tag name for this section in the assembled prompt.
    /// </summary>
    public const string Tag = "shell";

    /// <summary>
    /// The ordering position for this section within the prompt pipeline.
    /// Placed after tool-enforcement (32) to build on established tool-call behavior.
    /// </summary>
    public const int SectionOrder = 35;

    private static readonly string[] Lines =
    [
        "Prefer writing a temporary script file and executing it over chaining many individual shell commands.",
        "Combine related operations into a single shell invocation where possible.",
        "Avoid repeated read-modify-write cycles — batch edits into one tool call.",
        "Never use backtick line continuations in shell commands — they break across platforms.",
        "Use single quotes for strings containing special characters ($, @, |, ;).",
        "For complex multi-step operations, write a .ps1 or .sh script to a temp location and execute it."
    ];

    /// <summary>
    /// Creates a <see cref="LambdaPromptSection"/> for shell-efficiency guidance.
    /// </summary>
    public static LambdaPromptSection Create() =>
        new(SectionOrder, static _ => Lines, sectionId: Id, xmlTag: Tag);
}
