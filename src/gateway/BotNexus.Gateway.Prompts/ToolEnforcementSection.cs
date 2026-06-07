namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Provides the built-in tool-enforcement prompt section that instructs the model
/// to execute tools immediately rather than describing what it would do.
/// </summary>
public static class ToolEnforcementSection
{
    /// <summary>
    /// The stable section identifier used for override resolution.
    /// </summary>
    public const string Id = "tool-enforcement";

    /// <summary>
    /// The ordering position for this section within the prompt pipeline.
    /// Placed early (after safety at ~20) to establish tool behavior before content sections.
    /// </summary>
    public const int SectionOrder = 32;

    private static readonly string[] Lines =
    [
        "## Tool Enforcement",
        "When a task requires a tool call, execute the tool immediately.",
        "Do not describe what you would do — do it.",
        "Do not ask for confirmation before using tools unless the tool is destructive or the user explicitly asked for a plan.",
        "If multiple independent tool calls are needed, batch them in a single response.",
        "Never simulate or fabricate tool output — always call the real tool."
    ];

    /// <summary>
    /// Creates a <see cref="LambdaPromptSection"/> for tool-enforcement guidance.
    /// </summary>
    public static LambdaPromptSection Create() =>
        new(SectionOrder, static _ => Lines, sectionId: Id);
}
