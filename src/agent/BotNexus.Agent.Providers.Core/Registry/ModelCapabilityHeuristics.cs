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

    /// <summary>
    /// True for models that use the adaptive (effort-based) thinking protocol rather than an
    /// explicit token budget: Opus 4.6+ (which now includes Opus 5 and beyond) and Sonnet 4.6+.
    /// Shared by the Anthropic-direct and Copilot Messages providers so the same model never gets
    /// two different request shapes depending on which transport served it.
    /// </summary>
    /// <param name="modelId">The provider model id.</param>
    /// <returns>True when the model expects adaptive thinking.</returns>
    public static bool IsAdaptiveThinkingModel(string? modelId) =>
        ModelFamilyVersion.IsAtLeast(modelId, "opus", OpusAdaptiveMajor, OpusAdaptiveMinor) ||
        ModelFamilyVersion.IsAtLeast(modelId, "sonnet", SonnetAdaptiveMajor, SonnetAdaptiveMinor);

    /// <summary>
    /// True for models that expose the ExtraHigh / Max thinking tiers: Claude Opus 4.6+ and
    /// GPT 5.2+. Used by both the config-declared (dynamic) and Copilot-discovered registration
    /// paths so the two agree on any given id.
    /// </summary>
    /// <param name="modelId">The provider model id.</param>
    /// <returns>True when the model supports the top thinking tiers.</returns>
    public static bool SupportsExtraHighThinking(string? modelId) =>
        ModelFamilyVersion.IsAtLeast(modelId, "opus", OpusAdaptiveMajor, OpusAdaptiveMinor) ||
        ModelFamilyVersion.IsAtLeast(modelId, "gpt", GptExtraHighMajor, GptExtraHighMinor);

    /// <summary>
    /// True for model families known to support a thinking/reasoning override: Claude Opus/Sonnet
    /// 4+, GPT-5+, the o3/o4 series, Gemini 3+ and Grok-code.
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

        return false;
    }
}
