namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Detects the model family from a model identifier string.
/// Used by <see cref="ModelGuidanceSection"/> to select per-family prompt defaults.
/// </summary>
public static class ModelFamilyDetector
{
    /// <summary>Claude family (Anthropic).</summary>
    public const string Claude = "claude";

    /// <summary>GPT family (OpenAI).</summary>
    public const string Gpt = "gpt";

    /// <summary>Gemini family (Google).</summary>
    public const string Gemini = "gemini";

    /// <summary>Copilot family (GitHub).</summary>
    public const string Copilot = "copilot";

    /// <summary>DeepSeek family.</summary>
    public const string DeepSeek = "deepseek";

    /// <summary>Qwen family (Alibaba).</summary>
    public const string Qwen = "qwen";

    /// <summary>Llama family (Meta).</summary>
    public const string Llama = "llama";

    /// <summary>Unknown/unrecognized model family.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Maps a provider identifier to the model family that provider exclusively serves.
    /// Only providers whose entire catalog belongs to one family appear here: a provider that
    /// serves models from several vendors (openrouter, huggingface, a local gateway) proves
    /// nothing about the family and is deliberately absent, so it still resolves <see cref="Unknown"/>.
    /// </summary>
    private static readonly Dictionary<string, string> ProviderFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = Claude,
        ["openai"] = Gpt,
        ["azure-openai-responses"] = Gpt,
        ["google"] = Gemini,
        ["deepseek"] = DeepSeek,
        ["github-copilot"] = Copilot,
        ["github-copilot-completions"] = Copilot,
        ["github-copilot-messages"] = Copilot,
    };

    /// <summary>
    /// Determines the model family from a model identifier, falling back to the provider identity
    /// when the id alone does not resolve a family.
    /// </summary>
    /// <remarks>
    /// Matching the model id alone misses models served under vanity ids that carry no family
    /// substring (#3104) -- those sessions silently lost their family guidance because
    /// <see cref="ModelGuidanceSection"/> drops the whole section on <see cref="Unknown"/>.
    /// The model id is still consulted first and still wins whenever it resolves, so every
    /// existing id-only mapping is unchanged; the provider is consulted only as a fallback,
    /// and only for providers that serve exactly one family.
    /// </remarks>
    /// <param name="modelId">The model identifier (e.g. "claude-sonnet-4-20250514", "gpt-4o", "gemini-2.5-pro").</param>
    /// <param name="providerId">Optional provider identifier (e.g. "anthropic", "github-copilot").</param>
    /// <returns>One of the family constants, or <see cref="Unknown"/> if no match.</returns>
    public static string GetModelFamily(string? modelId, string? providerId = null)
    {
        // A blank model id identifies no model at all; a known provider must not invent a family
        // for it, or every unconfigured run would inherit that provider's guidance.
        if (string.IsNullOrWhiteSpace(modelId))
            return Unknown;

        var id = modelId.AsSpan();

        if (id.Contains("claude", StringComparison.OrdinalIgnoreCase))
            return Claude;

        if (id.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("o1", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("o3", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("o4", StringComparison.OrdinalIgnoreCase))
            return Gpt;

        if (id.Contains("gemini", StringComparison.OrdinalIgnoreCase))
            return Gemini;

        if (id.Contains("copilot", StringComparison.OrdinalIgnoreCase))
            return Copilot;

        if (id.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            return DeepSeek;

        if (id.Contains("qwen", StringComparison.OrdinalIgnoreCase))
            return Qwen;

        if (id.Contains("llama", StringComparison.OrdinalIgnoreCase))
            return Llama;

        if (!string.IsNullOrWhiteSpace(providerId) &&
            ProviderFamilies.TryGetValue(providerId.Trim(), out var providerFamily))
            return providerFamily;

        return Unknown;
    }
}
