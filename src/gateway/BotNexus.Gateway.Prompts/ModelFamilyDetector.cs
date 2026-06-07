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
    /// Determines the model family from a model identifier.
    /// Detection is case-insensitive and matches on common prefixes and substrings.
    /// </summary>
    /// <param name="modelId">The model identifier (e.g. "claude-sonnet-4-20250514", "gpt-4o", "gemini-2.5-pro").</param>
    /// <returns>One of the family constants, or <see cref="Unknown"/> if no match.</returns>
    public static string GetModelFamily(string? modelId)
    {
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

        return Unknown;
    }
}
