namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Provides the built-in sub-agent scoping prompt section (#2444).
/// </summary>
/// <remarks>
/// <para>
/// The platform prompt already tells agents to delegate ("if a task is more complex or takes
/// longer, spawn a sub-agent") but said nothing about how to SCOPE one. That asymmetry produces
/// the most expensive failure mode the runtime offers: a sub-agent that reaches its
/// <c>timeoutSeconds</c> ceiling loses its entire accumulated context and commits nothing. There
/// is no partial credit, so every token it spent is written off - not just the last minute.
/// </para>
/// <para>
/// The guidance here is therefore addressed to the ORCHESTRATOR, not the worker: bundling
/// implement, build, test, visual evidence and docs into one dispatch serialises five stages
/// behind one budget and one failure point, and nothing steers away from the 3600s maximum unless
/// a smaller default is stated explicitly.
/// </para>
/// </remarks>
public static class SubAgentScopingSection
{
    /// <summary>
    /// The stable section identifier used for override resolution.
    /// </summary>
    public const string Id = "subagent-scoping";

    /// <summary>
    /// The XML tag name for this section in the assembled prompt.
    /// </summary>
    public const string Tag = "subagent_scoping";

    /// <summary>
    /// The ordering position for this section within the prompt pipeline.
    /// Placed just after skills-guidance (55) so it lands with the other behavioural guidance,
    /// once the agent already knows which tools and skills it has.
    /// </summary>
    public const int SectionOrder = 57;

    /// <summary>
    /// The dispatch tool whose presence makes this guidance actionable. An agent that cannot spawn
    /// a sub-agent must not pay tokens for advice on how to scope one.
    /// </summary>
    public const string SpawnToolName = "spawn_subagent";

    private static readonly string[] Lines =
    [
        "A sub-agent that hits its `timeoutSeconds` ceiling loses its ENTIRE accumulated context and commits nothing. There is no partial credit — every token it spent is written off, not just the last minute. That failure is caused by the orchestrator's scoping, never by the worker.",
        "Budget by stage, not by feature. Default `timeoutSeconds` is 1500 (25 minutes), NOT the 3600 maximum. If a stage genuinely seems to need an hour, it is really two stages — split it.",
        "Never bundle implement + build + test + visual evidence + docs into one dispatch. That is five stages, not one task, and the later stages cannot even begin until the earlier ones are green.",
        "Measure before scoping. Build or run the affected project ONCE yourself first: a project that builds in seconds reframes the whole scoping decision, and cheap orchestrator reads and builds beat guessing.",
        "State what is ALREADY DONE in every brief, with real counts (e.g. \"the build is clean; the unit suite is green at 1054 passed, 0 failed\"). A worker not told this re-derives it from scratch and burns its budget re-establishing facts you already have.",
        "Commit between stages so a timeout can never lose work, and snapshot with `git diff HEAD > tmp/<issue>-snapshot.patch` before a ceiling is reachable. Do not trust a timing-out worker to preserve its own progress.",
        "Tell the worker to fail fast on infrastructure: if the harness or environment will not start, STOP EARLY and report rather than spending the budget fighting it. A fast honest failure is worth more than a timeout.",
        "Dispatch sequentially when workers share a worktree or working directory; run them in parallel only when the scopes are genuinely disjoint. Two workers in one worktree is how branches get corrupted.",
        "Never let a worker weaken, skip, or delete an assertion to go green — say so explicitly in every test-stage brief and require it to stop and report instead.",
        "Do the cheap work yourself. Committing, a single-project build, reading a diff — these cost the orchestrator seconds and would cost a worker its whole context.",
        "Recovering from a timed-out worker: do NOT reimplement. The files it wrote survive on disk — commit them, build, and dispatch only the REMAINING stages."
    ];

    /// <summary>
    /// Creates a <see cref="LambdaPromptSection"/> for sub-agent scoping guidance.
    /// The section is only included when sub-agent dispatch tooling is actually available.
    /// </summary>
    public static LambdaPromptSection Create() =>
        new(SectionOrder, static _ => Lines, sectionId: Id, shouldIncludeFunc: HasSpawnTool, xmlTag: Tag);

    private static bool HasSpawnTool(PromptContext context) =>
        context.AvailableTools.Contains(SpawnToolName);
}
