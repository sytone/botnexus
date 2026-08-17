using System.Text.RegularExpressions;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Gateway.Prompts;

/// <summary>
/// A parsed model-specific instruction-file suffix, e.g. the <c>claude-opus-4-8</c> in
/// <c>AGENTS.claude-opus-4-8.md</c> (#2435).
/// </summary>
/// <param name="BaseFileName">The file name with the suffix removed, e.g. <c>AGENTS.md</c>.</param>
/// <param name="Suffix">The raw suffix segment, e.g. <c>claude-opus-4-8</c>.</param>
/// <param name="NameTokens">The leading non-numeric tokens, e.g. <c>claude</c> and <c>opus</c>.</param>
/// <param name="Major">The major version component when the suffix carries one.</param>
/// <param name="Minor">The minor version component when the suffix carries one.</param>
public sealed record ContextFileVariantSuffix(
    string BaseFileName,
    string Suffix,
    IReadOnlyList<string> NameTokens,
    int? Major,
    int? Minor);

/// <summary>
/// Resolves model-specific variants of workspace instruction files by filename suffix (#2435).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a suffix grammar.</b> One workspace is read by conversations pinned to different models.
/// <c>AGENTS.gpt-5-6.md</c> lets the GPT conversation read different instructions from the Claude
/// one without forking the workspace, resolved on the SAME specificity ladder the
/// <c>[PromptVariant]</c> attributes use: family, then family+major, then family+major+minor, with
/// the base file as the always-present final fallback.
/// </para>
/// <para>
/// <b>The grammar is deliberately strict</b> and is the SAME grammar
/// <see cref="PromptVariantRegistry"/> enforces on <c>Family</c>/<c>Version</c>: lowercase
/// alphanumerics with a single <c>-</c> between tokens, and <c>.</c> only as a segment delimiter.
/// Agents author these files, so a divergent grammar guarantees a file that looks right and is
/// never read. Anything the grammar rejects — an uppercase suffix, a doubled separator, a second
/// middle segment — is NOT a variant at all: it is an ordinary file, and the base file continues
/// to be used. Failing to a visible base beats silently loading the wrong instructions.
/// </para>
/// <para>
/// A grammatically valid suffix naming a family the active model does not belong to
/// (<c>AGENTS.mistral.md</c> on a Claude conversation) simply does not match, which is the same
/// outcome by a different route. Version parsing goes through <see cref="ModelFamilyVersion"/>
/// (#2374) — there is deliberately no second version parser in the tree.
/// </para>
/// </remarks>
public static class ContextFileVariants
{
    /// <summary>
    /// The shared token grammar, identical to <c>PromptVariantRegistry.TokenGrammar</c>. Kept
    /// character-for-character in step so a family spelled one way in an attribute and another way
    /// on disk cannot resolve differently; <c>PromptVariantSuffixGrammarTests</c> asserts the pair.
    /// </summary>
    private const string TokenGrammarPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    /// <summary>At most a major and a minor: a third numeric token is not a version we model.</summary>
    private const int MaxVersionTokens = 2;

    private static readonly Regex TokenGrammar = new(TokenGrammarPattern, RegexOptions.Compiled);

    /// <summary>The shared token grammar pattern, exposed so the conformance test can compare it.</summary>
    public static string GrammarPattern => TokenGrammarPattern;

    /// <summary>
    /// Attempts to read a model-variant suffix out of <paramref name="fileName"/>.
    /// </summary>
    /// <param name="fileName">A bare file name (no directory), e.g. <c>AGENTS.gpt-5.md</c>.</param>
    /// <param name="suffix">The parsed suffix when this returns true; otherwise <see langword="null"/>.</param>
    /// <returns>True when the name is <c>&lt;stem&gt;.&lt;valid-suffix&gt;.&lt;ext&gt;</c>.</returns>
    public static bool TryParse(string? fileName, out ContextFileVariantSuffix? suffix)
    {
        suffix = null;
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        // Exactly three segments: stem, suffix, extension. A second middle segment
        // (AGENTS.gpt.5.md) is ambiguous against the extension and is rejected outright rather
        // than guessed at.
        var segments = fileName.Split('.');
        if (segments.Length != 3)
            return false;

        var (stem, candidate, extension) = (segments[0], segments[1], segments[2]);
        if (stem.Length == 0 || extension.Length == 0)
            return false;

        if (!TokenGrammar.IsMatch(candidate))
            return false;

        var tokens = candidate.Split('-');

        List<string> nameTokens = [];
        List<int> versionTokens = [];
        foreach (var token in tokens)
        {
            if (token.All(char.IsAsciiDigit))
            {
                versionTokens.Add(int.Parse(token, System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }

            // A name token after a numeric one is not a shape the ladder has a meaning for
            // (there is no "version then family" rung), so it is not a variant.
            if (versionTokens.Count > 0)
                return false;

            nameTokens.Add(token);
        }

        if (nameTokens.Count == 0 || versionTokens.Count > MaxVersionTokens)
            return false;

        suffix = new ContextFileVariantSuffix(
            $"{stem}.{extension}",
            candidate,
            nameTokens,
            versionTokens.Count > 0 ? versionTokens[0] : null,
            versionTokens.Count > 1 ? versionTokens[1] : null);
        return true;
    }

    /// <summary>
    /// Returns the BASE file name for <paramref name="fileName"/>: the name itself when it carries
    /// no valid variant suffix, otherwise the name with the suffix removed.
    /// </summary>
    /// <param name="fileName">A bare file name (no directory).</param>
    /// <returns>The base file name.</returns>
    public static string GetBaseFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return TryParse(fileName, out var suffix) && suffix is not null ? suffix.BaseFileName : fileName;
    }

    /// <summary>
    /// Scores how specifically <paramref name="suffix"/> matches the active model, or
    /// <see langword="null"/> when it does not match at all.
    /// </summary>
    /// <remarks>
    /// Every name token must be present in the model id on a token boundary, or be the detected
    /// family — the second clause is what lets <c>AGENTS.copilot.md</c> match a vanity model id
    /// served by a single-family provider. A declared version must equal the parsed one; a suffix
    /// declaring only a major ignores the model's minor, which is the whole point of that rung.
    /// </remarks>
    /// <param name="suffix">The parsed suffix.</param>
    /// <param name="modelId">The active model id.</param>
    /// <param name="providerId">The active provider id, used only for family fallback.</param>
    /// <returns>A specificity score (higher is more specific), or null when unmatched.</returns>
    public static int? Score(ContextFileVariantSuffix suffix, string? modelId, string? providerId = null)
    {
        ArgumentNullException.ThrowIfNull(suffix);

        if (string.IsNullOrWhiteSpace(modelId) && string.IsNullOrWhiteSpace(providerId))
            return null;

        var family = ModelFamilyDetector.GetModelFamily(modelId, providerId);

        foreach (var token in suffix.NameTokens)
        {
            var matched = ModelFamilyVersion.ContainsFamilyToken(modelId, token)
                || (!string.Equals(family, ModelFamilyDetector.Unknown, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(family, token, StringComparison.OrdinalIgnoreCase));

            if (!matched)
                return null;
        }

        // 10 per name token so an extra name token always outranks a version component, matching
        // the attribute ladder where family+model is more specific than family+version.
        var score = suffix.NameTokens.Count * 10;

        if (suffix.Major is null)
            return score;

        // The LAST name token is the version anchor: in claude-opus-4-8 the version hangs off
        // "opus", not off "claude".
        var anchor = suffix.NameTokens[^1];
        if (!ModelFamilyVersion.TryParse(modelId, anchor, out var parsed))
            return null;

        if (parsed.Major != suffix.Major.Value)
            return null;

        score += 2;

        if (suffix.Minor is null)
            return score;

        return parsed.Minor == suffix.Minor.Value ? score + 1 : null;
    }

    /// <summary>
    /// Picks the most specific variant of <paramref name="baseFileName"/> present in
    /// <paramref name="candidateFileNames"/> for the active model, falling back to
    /// <paramref name="baseFileName"/> itself.
    /// </summary>
    /// <param name="candidateFileNames">The bare file names present in the same directory.</param>
    /// <param name="baseFileName">The bare base file name, e.g. <c>AGENTS.md</c>.</param>
    /// <param name="modelId">The active model id.</param>
    /// <param name="providerId">The active provider id.</param>
    /// <returns>The winning file name; never null.</returns>
    public static string Resolve(
        IEnumerable<string> candidateFileNames,
        string baseFileName,
        string? modelId,
        string? providerId = null)
    {
        ArgumentNullException.ThrowIfNull(candidateFileNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseFileName);

        string? winner = null;
        var bestScore = 0;

        foreach (var candidate in candidateFileNames)
        {
            if (!TryParse(candidate, out var suffix) || suffix is null)
                continue;

            if (!string.Equals(suffix.BaseFileName, baseFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var score = Score(suffix, modelId, providerId);
            if (score is null || score.Value < bestScore)
                continue;

            // Deterministic tie-break: two suffixes of equal specificity (both single-family, say)
            // must not resolve by directory-enumeration order, which is filesystem-dependent.
            if (score.Value == bestScore && winner is not null
                && string.CompareOrdinal(candidate, winner) >= 0)
                continue;

            winner = candidate;
            bestScore = score.Value;
        }

        return winner ?? baseFileName;
    }
}
