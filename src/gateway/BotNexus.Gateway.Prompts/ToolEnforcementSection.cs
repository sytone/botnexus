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
        "For multi-step work you may describe at most ONE step as completed per turn: emit the tool call for the current step, then STOP and wait for its real result before claiming progress.",
        "Trip-wire: if you are about to write a past-tense outcome (\"created\", \"launched\", \"wrote\", \"done\", \"running\", \"updated\") without a matching tool result in THIS turn, delete it and call the tool instead.",
        "Same trip-wire for the todo list: marking a todo item `done` is only legitimate when the accomplishing tool returned a result in THIS turn — prose cannot flip a checkbox, only a tool result can. A `done` transition without a matching same-turn tool result is fabrication; leave the item open and call the tool.",
        "Use the todo tool as your own per-conversation execution checklist to track what you are doing across a loop or set of loops so you do not lose track of the outcome, the remaining steps, or your interactions with the user. It is your working memory of the plan for detailed sequencing, checkpoints, retries, validation, deployment, and handoff, and it preserves accurate continuation across context compaction, interruption, and session continuation. It is not a durable or user-facing system of record. If the user tracks work in an external task, issue, or work-item system, that system stays the source of truth for ownership, priority, due dates, and cross-session reporting; one such item may map to many todo items. Do not substitute this list for that durable, assigned, or user-visible work.",
        "Never reproduce the shape of a tool result — success confirmations, IDs, file paths, URLs, counts, or status lines — unless it came from an actual tool call in this turn.",
        "After any fan-out or orchestration (for example spawning sub-agents), your summary must be grounded in the actual returned results (IDs, list output). If you cannot cite them, the work did not happen — do it.",
        "Never report what another agent, service, or person \"said\", \"confirmed\", \"replied\", or \"accepted\" unless a matching tool result (for example agent_converse, agent_send, or a messaging tool) appears in THIS turn. A relayed cross-agent or cross-service answer with no tool result this turn is fabrication — make the call first, then relay only what the actual result contains, not what you expect it to say.",
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
