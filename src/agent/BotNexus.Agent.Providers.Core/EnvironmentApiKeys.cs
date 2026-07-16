namespace BotNexus.Agent.Providers.Core;

/// <summary>
/// Resolve API keys from environment variables.
/// Port of pi-mono's env-api-keys.ts.
/// </summary>
public static class EnvironmentApiKeys
{
    private static readonly Dictionary<string, string> EnvMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = "OPENAI_API_KEY",
        ["azure-openai-responses"] = "AZURE_OPENAI_API_KEY",
        ["google"] = "GEMINI_API_KEY",
        ["groq"] = "GROQ_API_KEY",
        ["cerebras"] = "CEREBRAS_API_KEY",
        ["xai"] = "XAI_API_KEY",
        ["openrouter"] = "OPENROUTER_API_KEY",
        ["vercel-ai-gateway"] = "AI_GATEWAY_API_KEY",
        ["zai"] = "ZAI_API_KEY",
        ["mistral"] = "MISTRAL_API_KEY",
        ["minimax"] = "MINIMAX_API_KEY",
        ["minimax-cn"] = "MINIMAX_CN_API_KEY",
        ["huggingface"] = "HF_TOKEN",
        ["opencode"] = "OPENCODE_API_KEY",
        ["opencode-go"] = "OPENCODE_API_KEY",
        ["kimi-coding"] = "KIMI_API_KEY",
    };

    /// <summary>
    /// Executes get api key.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>
    /// The resolved API key, or <c>null</c> when no non-blank value is configured.
    /// A set-but-blank environment variable (e.g. <c>OPENAI_API_KEY=""</c>) is treated
    /// as unconfigured so callers' <c>?? GetApiKey(...) ?? ""</c> fallbacks are not masked
    /// and multi-var priority chains are not short-circuited by an empty leading candidate.
    /// </returns>
    public static string? GetApiKey(string provider)
    {
        // GitHub Copilot: try multiple env vars in priority order, skipping blank values.
        if (string.Equals(provider, "github-copilot", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonBlank(
                Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN"),
                Environment.GetEnvironmentVariable("GH_TOKEN"),
                Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        }

        // Anthropic: OAuth token takes precedence over API key, skipping blank values.
        if (string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonBlank(
                Environment.GetEnvironmentVariable("ANTHROPIC_OAUTH_TOKEN"),
                Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        }

        if (EnvMap.TryGetValue(provider, out var envVar))
        {
            var v = Environment.GetEnvironmentVariable(envVar);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        return null;
    }

    // Returns the first value that is not null/whitespace, else null. Used to coalesce
    // provider env-var priority chains over genuinely-configured (non-blank) values so a
    // set-but-empty leading variable falls through to the next candidate.
    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}
