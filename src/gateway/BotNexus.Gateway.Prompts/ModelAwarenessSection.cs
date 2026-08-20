namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Provides the built-in <c>model-awareness</c> prompt section (#2436): the instruction that tells
/// an agent it is ONE of several model families the platform serves, that its instructions were
/// resolved for it rather than written universally, and that an edit to a base instruction file is
/// a contract change it must consciously classify.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a prompt section can be the right shape here.</b> The regression this section exists to
/// prevent is structural, not a discipline failure: an agent cannot self-detect that its prose is
/// shaped by its own family, because that shape is simply what "clear" looks like from inside its
/// own context. Naming the failure mode explicitly is the cheapest intervention that changes the
/// question the agent asks itself from "is this good guidance?" to "is this guidance true for
/// everyone, or only for me?".
/// </para>
/// <para>
/// <b>The section is itself subject to the ladder.</b> Every rung is declared with
/// <see cref="PromptVariantAttribute"/> and resolved through the startup-frozen
/// <see cref="PromptVariantRegistry"/>, exactly like <c>model-guidance</c>. That is deliberate and
/// not merely symmetry: a warning about family-shaped prose that could not itself be worded
/// differently per family would be asserting something it does not practise, and the default rung
/// being mandatory is what stops an unrecognised model receiving silence.
/// </para>
/// <para>
/// The section is paired with the <c>model_profile</c> tool, which turns "not all models are equal"
/// from an assertion into something an agent can QUERY before editing. Without the tool the
/// instruction leaves the agent guessing what variants exist; without the instruction the agent has
/// no reason to call the tool.
/// </para>
/// </remarks>
public static class ModelAwarenessSection
{
    /// <summary>The stable section identifier used for override and variant resolution.</summary>
    public const string Id = "model-awareness";

    /// <summary>The XML tag name for this section in the assembled prompt.</summary>
    public const string Tag = "model_awareness";

    /// <summary>
    /// The ordering position within the prompt pipeline. Placed at 136, immediately after
    /// <c>model-guidance</c> (135), so the "here is how you behave" rules and the "here is why those
    /// rules are yours specifically" framing read as one block.
    /// </summary>
    public const int SectionOrder = 136;

    /// <summary>The tool name this section points the agent at for pre-edit discovery.</summary>
    public const string DiscoveryToolName = "model_profile";

    /// <summary>Stable rule ids, so a family rung can target one rule rather than restate the set.</summary>
    internal static class Rules
    {
        /// <summary>The agent is one of several families; its instructions were resolved, not universal.</summary>
        public const string ResolvedNotUniversal = "resolved-not-universal";

        /// <summary>Instructions resolve on a specificity ladder.</summary>
        public const string SpecificityLadder = "specificity-ladder";

        /// <summary>A base-file edit must be classified agnostic vs model-specific.</summary>
        public const string ClassifyBaseFileEdits = "classify-base-file-edits";

        /// <summary>Query the discovery tool before editing a base instruction file.</summary>
        public const string QueryBeforeEditing = "query-before-editing";

        /// <summary>The variant filename grammar.</summary>
        public const string VariantNaming = "variant-naming";

        /// <summary>Family-shaped prose reads as plain good writing from inside that family.</summary>
        public const string FamilyShapedProse = "family-shaped-prose";
    }

    /// <summary>
    /// The DEFAULT rung: true of every model the platform serves. Mandatory -- an unrecognised
    /// family resolves here, and silence is the failure this ladder exists to remove (#2433).
    /// </summary>
    /// <returns>The default instruction rules.</returns>
    [PromptVariant(Id)]
    internal static IReadOnlyList<PromptRule> Default() =>
    [
        new(Rules.ResolvedNotUniversal,
            "You are one of several model families BotNexus serves. The instructions you are reading were RESOLVED for the model you are running on — they are not a universal statement about how every agent behaves."),
        new(Rules.SpecificityLadder,
            "Instruction files and prompt sections resolve on a specificity ladder: default, then model family, then family plus version. A more specific rung overlays the one beneath it rather than replacing it."),
        new(Rules.ClassifyBaseFileEdits,
            "A base instruction file (the workspace convention files such as AGENTS.md) is the CONTRACT; a model variant is the dialect. Before editing a base file, decide explicitly whether the change is agnostic (belongs in the base) or model-specific (belongs in a variant). Do not put a rule that is only true for you into a base file."),
        new(Rules.QueryBeforeEditing,
            $"Call `{DiscoveryToolName}` before editing a base instruction file. It reports your family and version, the capabilities your provider declares, which variant rungs resolved this turn, and which variant files already exist — so you can answer the agnostic-vs-specific question from data rather than from intuition."),
        new(Rules.VariantNaming,
            "A model-specific instruction file is named `<stem>.<suffix>.<ext>`, e.g. `AGENTS.gpt-5.md` or `AGENTS.claude-opus-4-8.md`. The suffix is lowercase alphanumerics separated by single hyphens; anything the grammar rejects is not a variant at all and is silently never read.")
    ];

    /// <summary>
    /// Claude overlays the default with the failure mode that produced this epic: Claude-shaped
    /// prose is what "clear" looks like from inside a Claude context, so fluency is not evidence of
    /// agnosticism.
    /// </summary>
    /// <returns>The Claude overlay rules.</returns>
    [PromptVariant(Id, Family = ModelFamilyDetector.Claude)]
    internal static IReadOnlyList<PromptRule> Claude() =>
    [
        new(Rules.FamilyShapedProse,
            "Guidance that reads to you as simply well written is Claude-shaped by default — layered rationale, hedged qualifiers, long motivating preamble. Treat \"this is just clear writing\" as evidence you may be authoring a variant, not a base file.")
    ];

    /// <summary>
    /// GPT declares its rung explicitly even though it adds nothing today, so the ladder carries a
    /// greppable GPT entry rather than an accidental one and the next GPT-only rule has a home.
    /// </summary>
    /// <returns>The GPT overlay rules.</returns>
    [PromptVariant(Id, Family = ModelFamilyDetector.Gpt)]
    internal static IReadOnlyList<PromptRule> Gpt() => [];

    /// <summary>Creates a <see cref="LambdaPromptSection"/> for model-awareness.</summary>
    /// <returns>The configured section.</returns>
    public static LambdaPromptSection Create() =>
        new(SectionOrder, BuildLines, sectionId: Id, shouldIncludeFunc: ShouldInclude, xmlTag: Tag);

    private static bool ShouldInclude(PromptContext context) => BuildLines(context).Count > 0;

    private static IReadOnlyList<string> BuildLines(PromptContext context) =>
        PromptVariantRegistry.Shared.Resolve(
            Id,
            ModelFamilyDetector.GetModelFamily(
                context.Get<string>(ModelGuidanceSection.ModelIdExtensionKey),
                context.Get<string>(ModelGuidanceSection.ProviderIdExtensionKey)),
            context.Get<string>(ModelGuidanceSection.ModelIdExtensionKey));
}
