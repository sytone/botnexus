namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Provides the built-in model-guidance prompt section that injects per-model-family
/// behavioral defaults into the system prompt. Detects the model family from the model
/// identifier passed through <see cref="PromptContext.Extensions"/> and resolves the
/// instruction set from the startup-frozen <see cref="PromptVariantRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Before #2433 this class WAS the model-adaptation surface: three hardcoded string arrays selected
/// by a <c>switch</c>, with <c>_ =&gt; []</c> as the fallback. That fell open -- an unrecognised
/// family silently received zero guidance -- and it made "the same rules, one line different" an
/// impossible thing to express, so the three arrays shared no intent at all.
/// </para>
/// <para>
/// Now every rung is DECLARED with <see cref="PromptVariantAttribute"/> and overlays the default by
/// stable rule id. The default rung is mandatory (the registry refuses to freeze without it), which
/// is what structurally removes the fail-open: an unknown model gets the conservative default.
/// </para>
/// </remarks>
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

    /// <summary>
    /// The <see cref="PromptContext.Extensions"/> key used to pass the provider identifier
    /// through to the section builder. Without it, a model served under a vanity id by a
    /// family-specific provider resolves <see cref="ModelFamilyDetector.Unknown"/> and loses
    /// its guidance silently (#3104).
    /// </summary>
    public const string ProviderIdExtensionKey = "providerId";

    /// <summary>Stable rule ids, so a family variant can target one rule rather than restate the set.</summary>
    internal static class Rules
    {
        /// <summary>Verify with a tool rather than answering from recall.</summary>
        public const string VerifyWithTools = "verify-with-tools";

        /// <summary>Read a file before describing its contents.</summary>
        public const string ReadBeforeAnswering = "read-before-answering";

        /// <summary>State uncertainty rather than confabulating.</summary>
        public const string StateUncertainty = "state-uncertainty";

        /// <summary>Check tool output rather than assuming success.</summary>
        public const string VerifyToolOutput = "verify-tool-output";

        /// <summary>Prefer targeted edits over whole-file rewrites.</summary>
        public const string PreferTargetedEdits = "prefer-targeted-edits";

        /// <summary>Keep edit match windows small.</summary>
        public const string SmallEditWindows = "small-edit-windows";

        /// <summary>Use extended thinking before acting.</summary>
        public const string UseExtendedThinking = "use-extended-thinking";

        /// <summary>Use absolute paths in file operations.</summary>
        public const string AbsolutePaths = "absolute-paths";

        /// <summary>Reference files from the workspace root.</summary>
        public const string WorkspaceRootPaths = "workspace-root-paths";

        /// <summary>Build each tool call only from that tool's own schema (#3375).</summary>
        public const string ToolSchemaFidelity = "tool-schema-fidelity";

        /// <summary>Stop retrying the same operation on the same target after two failures (#3375).</summary>
        public const string RetryCircuitBreaker = "retry-circuit-breaker";

        /// <summary>Checkpoint narration on an observable count, not a subjective judgement (#3375).</summary>
        public const string NarrationThreshold = "narration-threshold";

        /// <summary>Infer the intended task and complete the authorized scope.</summary>
        public const string CompleteAuthorizedTask = "complete-authorized-task";

        /// <summary>Make safe progress before asking about a material blocker.</summary>
        public const string ClarifyMaterialBlockers = "clarify-material-blockers";

        /// <summary>Explain a blocking skill rule without weakening the instruction hierarchy.</summary>
        public const string ExplainSkillConstraints = "explain-skill-constraints";

        /// <summary>Use the persona and lead with the useful result.</summary>
        public const string ResultFirstCommunication = "result-first-communication";

        /// <summary>Delegate independent bounded work using available tools.</summary>
        public const string BoundedDelegation = "bounded-delegation";

        /// <summary>Complete required verification without redundant broadening.</summary>
        public const string ProportionateVerification = "proportionate-verification";
    }

    /// <summary>
    /// The DEFAULT rung: conservative guidance that is true of every model. Any family the platform
    /// does not recognise resolves here, which is why it must never be empty (#2433).
    /// </summary>
    /// <returns>The default instruction rules.</returns>
    [PromptVariant(Id)]
    internal static IReadOnlyList<PromptRule> Default() =>
    [
        new(Rules.VerifyWithTools, "Never answer from memory when a tool can verify the answer — always check the source."),
        new(Rules.ReadBeforeAnswering, "When asked about file contents, always read the file rather than guessing from context."),
        new(Rules.StateUncertainty, "Be explicit about uncertainty — say when you are unsure rather than confabulating."),
        new(Rules.VerifyToolOutput, "Verify tool output carefully before proceeding — do not assume success without checking.")
    ];

    /// <summary>
    /// Claude overlays the default with edit-tool and extended-thinking guidance.
    /// </summary>
    /// <returns>The Claude overlay rules.</returns>
    [PromptVariant(Id, Family = ModelFamilyDetector.Claude)]
    internal static IReadOnlyList<PromptRule> Claude() =>
    [
        new(Rules.PreferTargetedEdits, "Prefer the edit tool over write for modifying existing files — it preserves context and is more precise."),
        new(Rules.SmallEditWindows, "When editing, use the smallest possible oldText/newText to target changes precisely."),
        new(Rules.UseExtendedThinking, "You have extended thinking capabilities — use them for complex reasoning before acting.")
    ];

    /// <summary>
    /// GPT overlays the default with three rules derived from a controlled A/B evaluation of a GPT
    /// model against a Claude model on an identical task with an identical system prompt (#3375).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rung was empty until #3375, on the reasoning that GPT's verification rules had become the
    /// shared default. The evaluation showed that is only half true: engineering output was
    /// equivalent, but the GPT run produced 165 history rows to 69, five tool errors to one, and one
    /// assistant message to five. Those are three distinct, reproducible failure shapes, and each is
    /// a SALIENCE gap rather than a capability gap -- the model complied immediately when the
    /// requirement was named explicitly and proximately.
    /// </para>
    /// <para>
    /// Wording therefore favours checkable conditions over subjective language. Each rule closes a
    /// specific observed loophole: the retry rule names the "but it looked different" escape (a
    /// changed match count) because the generic "change approach after two failures" guidance
    /// demonstrably failed to fire against it, and the narration rule carries a count because
    /// "narrate when it helps" evaluates to false for every individually-routine call in a run of
    /// 163 of them.
    /// </para>
    /// </remarks>
    /// <returns>The GPT overlay rules.</returns>
    [PromptVariant(Id, Family = ModelFamilyDetector.Gpt)]
    internal static IReadOnlyList<PromptRule> Gpt() =>
    [
        new(Rules.ToolSchemaFidelity, "Build every tool call using only the properties declared in that tool's own schema — never carry a parameter across from a similar tool, and never invent one. If the argument you want does not exist on the tool you selected, the tool selection is wrong, not the schema."),
        new(Rules.RetryCircuitBreaker, "After two failed attempts at the same operation on the same target, stop and change approach — a different match count, different whitespace, or a different anchor is the SAME strategy retried, not a new one. Re-read the current state of the target before attempting again."),
        new(Rules.NarrationThreshold, "Post a short progress message to the user at least once every ten tool calls, and at every phase boundary (investigation done, implementation done, validation done). Individually routine calls still accumulate into a long silent run — the trigger is the count, not your judgement of whether any single call was interesting.")
    ];

    /// <summary>
    /// GPT-6 overlays the inherited default and GPT rules with task-completion, clarification,
    /// communication, delegation, and verification guidance (#3917). This is a major-version
    /// opt-in, so minor releases inherit it without changing exact-version declaration semantics.
    /// </summary>
    /// <remarks>
    /// Adapted from OpenAI's prompting guide (Astra notes), pinned at:
    /// https://github.com/openai/codex/blob/008bbd5884122dc95aaece19ecfe0fc6a59dcf36/codex-rs/skills/src/assets/samples/openai-docs/references/prompting-guide.md
    /// These rules tune behavior, not provider capabilities, tool availability, or reasoning settings.
    /// </remarks>
    /// <returns>The additive GPT-6 overlay rules.</returns>
    [PromptVariant(Id, Family = ModelFamilyDetector.Gpt, Version = "6", MatchMajorVersion = true)]
    internal static IReadOnlyList<PromptRule> Gpt6() =>
    [
        new(Rules.CompleteAuthorizedTask, "Infer the intended outcome from the actual user request and context, and complete the whole authorized task — not just a capability acknowledgement or a plan."),
        new(Rules.ClarifyMaterialBlockers, "Do safe, authorized preparatory work before asking a question; ask when missing information materially blocks correct completion. Preserve explicit approval gates and never infer missing authority."),
        new(Rules.ExplainSkillConstraints, "When a skill blocks the task, report the exact relevant instruction and distinguish the rule from your interpretation. Follow the instruction hierarchy; a user request does not override system or developer instructions."),
        new(Rules.ResultFirstCommunication, "Follow the agent persona, lead with the useful result or evidence, and use plain language. Avoid canned phrases, unnecessary formatting, and verbosity that does not help the user."),
        new(Rules.BoundedDelegation, "Use available delegation tools for independent, bounded work when useful. Honor workspace isolation and concurrency constraints, and verify delegated results before relying on them; do not assume unavailable tools or capabilities."),
        new(Rules.ProportionateVerification, "Complete all required checks. Broaden or repeat verification only for new changes, failures, or unresolved concerns; never weaken required checks to save effort. Reuse already-read evidence only while it remains current and relevant, and report only what the evidence supports.")
    ];

    /// <summary>
    /// Gemini overlays the default with path-resolution guidance.
    /// </summary>
    /// <returns>The Gemini overlay rules.</returns>
    [PromptVariant(Id, Family = ModelFamilyDetector.Gemini)]
    internal static IReadOnlyList<PromptRule> Gemini() =>
    [
        new(Rules.AbsolutePaths, "Always use absolute paths in file operations — relative paths may resolve incorrectly."),
        new(Rules.WorkspaceRootPaths, "When referencing files, use the full path from the workspace root.")
    ];

    /// <summary>
    /// Creates a <see cref="LambdaPromptSection"/> for model-guidance.
    /// </summary>
    public static LambdaPromptSection Create() =>
        new(SectionOrder, BuildLines, sectionId: Id, shouldIncludeFunc: ShouldInclude, xmlTag: Tag);

    private static bool ShouldInclude(PromptContext context) => BuildLines(context).Count > 0;

    private static string ResolveFamily(PromptContext context) =>
        ModelFamilyDetector.GetModelFamily(
            context.Get<string>(ModelIdExtensionKey),
            context.Get<string>(ProviderIdExtensionKey));

    private static IReadOnlyList<string> BuildLines(PromptContext context) =>
        PromptVariantRegistry.Shared.Resolve(
            Id,
            ResolveFamily(context),
            context.Get<string>(ModelIdExtensionKey));
}
