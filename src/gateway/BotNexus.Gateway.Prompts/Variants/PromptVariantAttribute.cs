namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Declares that the annotated static member supplies the instruction rules for a prompt section,
/// optionally scoped to a model family and version (#2433).
/// </summary>
/// <remarks>
/// <para>
/// The annotated member must be a <c>static</c> parameterless method or a <c>static</c> property
/// returning <c>IReadOnlyList&lt;PromptRule&gt;</c>. It is invoked ONCE, while
/// <see cref="PromptVariantRegistry"/> is being frozen at startup; the resulting rules are copied
/// into an immutable lookup and the member is never touched again. Nothing on the prompt-build path
/// reflects, so the per-turn cost of model adaptation is a dictionary probe.
/// </para>
/// <para>
/// Declaring the family and version AT THE DECLARATION SITE is deliberate. The alternative --
/// a <c>switch</c> inside the section builder -- is what #2433 removes: it put the model-adaptation
/// policy somewhere a reader of the instruction text could not see it, and it silently emitted
/// NOTHING for any family the switch had never heard of.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class PromptVariantAttribute : Attribute
{
    /// <summary>
    /// Declares a variant for the given section.
    /// </summary>
    /// <param name="sectionId">
    /// The stable section id this variant contributes to, e.g. <see cref="ModelGuidanceSection.Id"/>.
    /// </param>
    public PromptVariantAttribute(string sectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);
        SectionId = sectionId;
    }

    /// <summary>The stable section id this variant contributes to.</summary>
    public string SectionId { get; }

    /// <summary>
    /// The model family this variant applies to, e.g. <c>gpt</c>. Leave <see langword="null"/> to
    /// declare the DEFAULT rung -- the conservative instruction set every unrecognised model
    /// receives. Must match the shared token grammar: lowercase alphanumerics with <c>-</c> between
    /// tokens.
    /// </summary>
    public string? Family { get; set; }

    /// <summary>
    /// The model version this variant applies to within <see cref="Family"/>, spelled in the same
    /// token grammar as the family (<c>5</c>, <c>4-6</c>). Parsed by
    /// <c>ModelFamilyVersion</c> -- there is deliberately no second version parser in the tree
    /// (#2374). Setting a version without a family is rejected at freeze time: a version means
    /// nothing without the family it versions.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// When <see langword="true"/>, this variant REPLACES the default rung outright instead of
    /// overlaying it rule-by-rule. The escape hatch for a family that genuinely needs a different
    /// instruction set; being a declaration-site property keeps that choice visible to whoever
    /// reads the instructions, rather than buried in resolution logic.
    /// </summary>
    public bool Replace { get; set; }
}
