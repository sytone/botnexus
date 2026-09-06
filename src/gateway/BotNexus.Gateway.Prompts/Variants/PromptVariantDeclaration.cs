using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Gateway.Prompts;

/// <summary>
/// One validated <see cref="PromptVariantAttribute"/> declaration, as retained by
/// <see cref="PromptVariantRegistry.Declarations"/> (#2434).
/// </summary>
/// <remarks>
/// This is the public, enumerable form of the variant corpus. #2434's premise is that making the
/// variants enumerable buys the structural tests almost for free: without it, a conformance suite
/// has to re-walk the attributes itself, and that second walk is free to drift away from the walk
/// the prompt path actually performs -- at which point the suite is asserting over its own copy of
/// the data rather than over the thing that ships.
/// </remarks>
/// <param name="SectionId">The stable section id this variant contributes to.</param>
/// <param name="Family">The model family, or <see langword="null"/> for the default rung.</param>
/// <param name="Version">The model version within <paramref name="Family"/>, or <see langword="null"/>.</param>
/// <param name="Replace">True when this rung discards everything beneath it instead of overlaying.</param>
/// <param name="Rules">The rules this rung declares, in declaration order.</param>
/// <param name="Site">The declaring member, as <c>Namespace.Type.Member</c>, for diagnostics.</param>
public sealed record PromptVariantDeclaration(
    string SectionId,
    string? Family,
    ModelVersion? Version,
    bool Replace,
    IReadOnlyList<PromptRule> Rules,
    string Site)
{
    /// <summary>
    /// True when this declaration matches every minor of <see cref="Version"/>'s major, rather
    /// than the exact version. Init-only to preserve the existing positional constructor.
    /// </summary>
    public bool MatchMajorVersion { get; init; }

    /// <summary>True when this declaration is the section's default rung.</summary>
    public bool IsDefault => Family is null;
}
