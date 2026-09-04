namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Contributes an ordered block of content to an assembled system prompt.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Status: wired. Decision recorded for #3667 - option (a), wire it, over (b), remove
/// it.</strong> #3539 found this contract had zero implementations AND no production collection
/// site at all: <c>SystemPromptBuilder</c> composed its <c>PromptPipeline</c> exclusively from
/// <c>Add(IPromptSection)</c> and never called <c>AddContributors</c>, so an implementation
/// registered in DI was constructed and then silently ignored. #3667 closed that gap.
/// </para>
/// <para>
/// Removal was rejected because the abstraction was never the broken part. <c>PromptPipeline</c>
/// already honoured contributors correctly once handed them - ordering, <c>Target</c> filtering,
/// <c>ShouldInclude</c> and heading emission are all covered by <c>PromptPrimitivesTests</c> - so
/// the defect was one missing call at the composer, not a design fault. Deleting it would also
/// have removed the only prompt extension seam available to a dynamically loaded extension
/// assembly, which cannot add an <c>IPromptSection</c> to a pipeline the gateway composes
/// internally.
/// </para>
/// <para>
/// The collection site is <c>WorkspaceContextBuilder</c>, which resolves
/// <c>IEnumerable&lt;IPromptContributor&gt;</c> from DI and passes it through
/// <c>SystemPromptParams.PromptContributors</c> to <c>PromptPipeline.AddContributors</c>. Register
/// an implementation in the host container and its contribution appears in the assembled system
/// prompt, ordered by <see cref="Priority"/> against the builder's section order keys. Note that
/// only contributors with a null <see cref="Target"/> render as standalone blocks today.
/// </para>
/// </remarks>
public interface IPromptContributor
{
    /// <summary>
    /// The section this contribution attaches to, or <c>null</c> for a standalone block ordered by
    /// <see cref="Priority"/>. Only standalone contributors are rendered today.
    /// </summary>
    PromptSection? Target { get; }

    /// <summary>
    /// Sort key for a standalone contribution, compared against the composer's section order keys.
    /// Overridden by <c>PromptContribution.Order</c> when that is supplied.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Whether this contributor participates in the prompt being assembled.
    /// </summary>
    bool ShouldInclude(PromptContext context);

    /// <summary>
    /// Produces the contribution for the given context.
    /// </summary>
    PromptContribution GetContribution(PromptContext context);
}
