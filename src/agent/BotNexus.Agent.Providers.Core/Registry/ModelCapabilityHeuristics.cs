namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// The single source of truth for Claude / GPT / Gemini capability gating by model version
/// (issue #2374). Before this existed, four separate files each carried their own hand-maintained
/// list of literal id substrings (<c>opus-4-6</c>, <c>opus-4.6</c>, <c>opus-4-8</c>, ...), which
/// meant every new model generation required editing all four AND silently failed closed until
/// somebody remembered to. <c>claude-opus-5</c> was classified by none of them.
/// <para>
/// Every predicate here is expressed as "family X at version >= N", parsed by
/// <see cref="ModelFamilyVersion"/>, so the next generation needs no code change at all.
/// </para>
/// <para>
/// <b>Unparseable Claude ids fail OPEN to modern behaviour.</b> When an id is recognisably Claude
/// but carries no readable version (<c>claude-opus-next</c>, <c>claude-opus-latest</c>,
/// <c>anthropic/claude-opus-preview</c>), the two possible defaults are not symmetric. Failing
/// CLOSED sends a brand-new generation the LEGACY token-budget thinking request -- a silent
/// downgrade that looks like a working model producing worse output, and which nobody notices until
/// somebody diffs request payloads. Failing OPEN sends the current-generation adaptive request,
/// which at worst produces a loud, immediate, fixable protocol error against a model we had never
/// heard of. Loud-and-wrong beats silent-and-degraded, and the direction of model releases is
/// always forward, so the newest protocol is the better guess for an unknown Claude id. This is the
/// same default the OpenCode audit arrived at independently. Fail-open is deliberately NOT a
/// blanket true: the id must contain a Claude family token on a token boundary
/// (<see cref="ModelFamilyVersion.ContainsFamilyToken"/>), so non-Claude vendors and substring
/// collisions such as <c>octopus5</c> still fail closed.
/// </para>
/// </summary>
public static class ModelCapabilityHeuristics
{
    /// <summary>Opus gained adaptive thinking and the extra-high tiers at 4.6.</summary>
    private const int OpusAdaptiveMajor = 4;

    /// <summary>Minor component of the Opus adaptive-thinking floor.</summary>
    private const int OpusAdaptiveMinor = 6;

    /// <summary>Sonnet gained adaptive thinking at 4.6.</summary>
    private const int SonnetAdaptiveMajor = 4;

    /// <summary>Minor component of the Sonnet adaptive-thinking floor.</summary>
    private const int SonnetAdaptiveMinor = 6;

    /// <summary>GPT gained the extra-high thinking tiers at 5.2.</summary>
    private const int GptExtraHighMajor = 5;

    /// <summary>Minor component of the GPT extra-high floor.</summary>
    private const int GptExtraHighMinor = 2;

    /// <summary>The Claude generation at which thinking/reasoning became available at all.</summary>
    private const int ClaudeReasoningMajor = 4;

    /// <summary>The GPT generation at which reasoning became available.</summary>
    private const int GptReasoningMajor = 5;

    /// <summary>The Gemini generation at which reasoning became available.</summary>
    private const int GeminiReasoningMajor = 3;

    /// <summary>The Claude vendor token. An id carrying it is Claude even when the version will not parse.</summary>
    private const string ClaudeToken = "claude";

    /// <summary>The Claude thinking-capable family tokens, in the order they are probed.</summary>
    private static readonly string[] ThinkingClaudeFamilies = ["opus", "sonnet"];

    /// <summary>
    /// True for models that use the adaptive (effort-based) thinking protocol rather than an
    /// explicit token budget: Opus 4.6+ (which now includes Opus 5 and beyond) and Sonnet 4.6+.
    /// Shared by the Anthropic-direct and Copilot Messages providers so the same model never gets
    /// two different request shapes depending on which transport served it.
    /// <para>
    /// A recognisably-Claude id whose version does not parse returns true: see the fail-open
    /// rationale on <see cref="ModelCapabilityHeuristics"/> (issue #2374). A new generation must not
    /// silently degrade to the legacy token-budget protocol just because its id spelling is new.
    /// </para>
    /// </summary>
    /// <param name="modelId">The provider model id.</param>
    /// <returns>True when the model expects adaptive thinking.</returns>
    public static bool IsAdaptiveThinkingModel(string? modelId)
    {
        if (ModelFamilyVersion.IsAtLeast(modelId, "opus", OpusAdaptiveMajor, OpusAdaptiveMinor) ||
            ModelFamilyVersion.IsAtLeast(modelId, "sonnet", SonnetAdaptiveMajor, SonnetAdaptiveMinor))
            return true;

        // Fail open to modern for an unversioned Claude id (claude-opus-next, claude-next).
        return IsUnversionedClaude(modelId, ThinkingClaudeFamilies);
    }

    /// <summary>
    /// True for models that expose the ExtraHigh / Max thinking tiers: Claude Opus 4.6+ and
    /// GPT 5.2+. Used by both the config-declared (dynamic) and Copilot-discovered registration
    /// paths so the two agree on any given id.
    /// <para>
    /// The fail-open here is narrower than <see cref="IsAdaptiveThinkingModel"/>: only an
    /// unversioned OPUS-class id (or a bare unversioned <c>claude-*</c>) qualifies, because Sonnet
    /// and Haiku have never carried the top tiers and guessing forward for them would widen a
    /// capability that was never theirs.
    /// </para>
    /// </summary>
    /// <param name="modelId">The provider model id.</param>
    /// <returns>True when the model supports the top thinking tiers.</returns>
    public static bool SupportsExtraHighThinking(string? modelId)
    {
        if (ModelFamilyVersion.IsAtLeast(modelId, "opus", OpusAdaptiveMajor, OpusAdaptiveMinor) ||
            ModelFamilyVersion.IsAtLeast(modelId, "gpt", GptExtraHighMajor, GptExtraHighMinor))
            return true;

        return IsUnversionedClaude(modelId, ["opus"]);
    }

    /// <summary>
    /// True for model families known to support a thinking/reasoning override: Claude Opus/Sonnet
    /// 4+, GPT-5+, the o3/o4 series, Gemini 3+ and Grok-code.
    /// <para>
    /// An unversioned Claude Opus/Sonnet id fails open to true for the same reason as
    /// <see cref="IsAdaptiveThinkingModel"/>: failing closed would strip reasoning from a new
    /// generation entirely, which is a far worse failure than offering it to a model that declines
    /// it (issue #2374).
    /// </para>
    /// </summary>
    /// <param name="modelId">The provider model id.</param>
    /// <returns>True when the family supports reasoning.</returns>
    public static bool IsReasoningModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        if (ModelFamilyVersion.IsAtLeast(modelId, "opus", ClaudeReasoningMajor) ||
            ModelFamilyVersion.IsAtLeast(modelId, "sonnet", ClaudeReasoningMajor))
            return true;

        if (ModelFamilyVersion.IsAtLeast(modelId, "gpt", GptReasoningMajor))
            return true;

        if (modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ModelFamilyVersion.IsAtLeast(modelId, "gemini", GeminiReasoningMajor))
            return true;

        if (modelId.StartsWith("grok-code", StringComparison.OrdinalIgnoreCase))
            return true;

        return IsUnversionedClaude(modelId, ThinkingClaudeFamilies);
    }

    /// <summary>
    /// True when the id is recognisably Claude -- it carries the <c>claude</c> vendor token on a
    /// token boundary -- but no version could be read for any of <paramref name="families"/>, and it
    /// does not name a Claude family OUTSIDE that set. The last condition is what stops
    /// <c>claude-haiku-next</c> inheriting an Opus/Sonnet capability: the id is Claude, but it has
    /// explicitly told us which family it is, and that family is not in scope.
    /// </summary>
    private static bool IsUnversionedClaude(string? modelId, string[] families)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        if (!ModelFamilyVersion.ContainsFamilyToken(modelId, ClaudeToken))
            return false;

        // If any in-scope family parsed a version, the numeric gate above already had its say and
        // decided against; do not second-guess it with a fail-open.
        foreach (var family in families)
        {
            if (ModelFamilyVersion.TryParse(modelId, family, out _))
                return false;
        }

        // A named but out-of-scope Claude family (haiku for the thinking tiers, or sonnet when only
        // opus is in scope) is a deliberate exclusion, not an unknown.
        foreach (var family in NamedClaudeFamilies)
        {
            if (!families.Contains(family, StringComparer.OrdinalIgnoreCase) &&
                ModelFamilyVersion.ContainsFamilyToken(modelId, family))
                return false;
        }

        return true;
    }

    /// <summary>Every Claude family token we can name; anything else in a claude-* id is unknown.</summary>
    private static readonly string[] NamedClaudeFamilies = ["opus", "sonnet", "haiku"];
}
