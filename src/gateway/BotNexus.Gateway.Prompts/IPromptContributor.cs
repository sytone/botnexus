namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Contributes an ordered block of content to an assembled system prompt.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Status: zero implementations, and no production collection site (decision recorded for
/// #3539).</strong> Nothing in this repository implements this interface outside a single test
/// double. More importantly, the only way a contributor can reach an assembled prompt is
/// <c>PromptPipeline.AddContributors</c>, and the sole production composer -
/// <c>SystemPromptBuilder</c> - builds its pipeline exclusively from <c>Add(IPromptSection)</c> and
/// never calls <c>AddContributors</c>. An implementation registered in DI today would therefore be
/// constructed and silently ignored, with no error and no log line.
/// </para>
/// <para>
/// That is a sharper state than merely "unused": <c>IApiContributor</c> at least has a wired
/// collection site that would run if something implemented it, whereas this contract is not
/// reachable at all from the production prompt path. The distinction matters to anyone reaching
/// for this interface expecting extension prompts to work.
/// </para>
/// <para>
/// Retained rather than removed pending that wiring: <c>PromptPipeline</c> honours contributors
/// correctly once they are handed to it (ordering, <c>ShouldInclude</c> and heading emission are
/// all covered by <c>PromptPrimitivesTests</c>), so the gap is one missing call at the composer,
/// not a broken abstraction. Anyone implementing this must also wire <c>AddContributors</c> into
/// <c>SystemPromptBuilder</c>, or their contribution will not appear.
/// </para>
/// </remarks>
public interface IPromptContributor
{
    PromptSection? Target { get; }

    int Priority { get; }

    bool ShouldInclude(PromptContext context);

    PromptContribution GetContribution(PromptContext context);
}
