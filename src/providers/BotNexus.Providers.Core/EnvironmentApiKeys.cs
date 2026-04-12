namespace BotNexus.Providers.Core;

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
    /// <returns>The get api key result.</returns>
    public static string? GetApiKey(string provider)
    {
        // GitHub Copilot: try multiple env vars in priority order
        if (string.Equals(provider, "github-copilot", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN")
                   ?? Environment.GetEnvironmentVariable("GH_TOKEN")
                   ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        }

        // Anthropic: OAuth token takes precedence over API key
        if (string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.GetEnvironmentVariable("ANTHROPIC_OAUTH_TOKEN")
                   ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        }

        if (EnvMap.TryGetValue(provider, out var envVar))
            return Environment.GetEnvironmentVariable(envVar);

        return null;
    }
}
