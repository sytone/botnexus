namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Provides the unified tool-use prompt section that combines execution bias,
/// tool enforcement, and tool call style guidance into a single cohesive block.
/// Wrapped in &lt;tool_use&gt; XML tags for improved model attention.
/// </summary>
public static class ToolEnforcementSection
{
    /// <summary>
    /// The stable section identifier used for override resolution.
    /// </summary>
    public const string Id = "tool-enforcement";

    /// <summary>
    /// The XML tag name for this section in the assembled prompt.
    /// </summary>
    public const string Tag = "tool_use";

    /// <summary>
    /// The ordering position for this section within the prompt pipeline.
    /// Placed early to establish tool behavior before content sections.
    /// </summary>
    public const int SectionOrder = 30;

    private static readonly string[] Lines =
    [
        "You MUST use your tools to take action — do not describe what you would do or plan to do without actually doing it.",
        "When you say you will perform an action, you MUST immediately make the corresponding tool call in the same response.",
        "Never end your turn with a promise of future action — execute it now.",
        "Keep working until the task is actually complete. Do not stop with a summary of what you plan to do next time.",
        "Every response should either (a) contain tool calls that make progress, or (b) deliver a final result to the user.",
        "Responses that only describe intentions without acting are not acceptable.",
        "If multiple independent tool calls are needed, batch them in a single response.",
        "Never simulate or fabricate tool output — always call the real tool.",
        "Do not ask for confirmation before using tools unless the tool is destructive or the user explicitly asked for a plan.",
        "Default: do not narrate routine, low-risk tool calls (just call the tool).",
        "Narrate only when it helps: multi-step work, complex/challenging problems, sensitive actions, or when the user explicitly asks.",
        "Keep narration brief and value-dense; avoid repeating obvious steps."
    ];

    /// <summary>
    /// Creates a <see cref="LambdaPromptSection"/> for the unified tool-use guidance.
    /// </summary>
    public static LambdaPromptSection Create() =>
        new(SectionOrder, static _ => Lines, sectionId: Id, xmlTag: Tag);
}
