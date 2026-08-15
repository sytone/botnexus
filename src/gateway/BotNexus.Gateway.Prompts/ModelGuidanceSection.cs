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
    /// GPT needs no additions today: the verification rules it used to carry ARE the default set now.
    /// The rung is declared anyway so the ladder has an explicit, greppable GPT entry rather than an
    /// accidental one, and so the next GPT-only rule has an obvious home.
    /// </summary>
    /// <returns>The GPT overlay rules.</returns>
    [PromptVariant(Id, Family = ModelFamilyDetector.Gpt)]
    internal static IReadOnlyList<PromptRule> Gpt() => [];

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
