using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Agent.Providers.GitHubModels;

/// <summary>
/// GitHub Models inference provider configuration and model catalog.
/// GitHub Models exposes an OpenAI-compatible completions endpoint at
/// <c>https://models.inference.ai.azure.com</c> and authenticates via the
/// <c>GITHUB_TOKEN</c> environment variable.
/// </summary>
/// <remarks>
/// This class registers GitHub Models into the global <see cref="ModelRegistry"/>.
/// Requests are handled by the existing <c>openai-compat</c> provider — no separate
/// provider registration is required.
/// <para>
/// Example config entry (<c>gateway.json</c>):
/// <code>
/// "providers": {
///   "github-models": {
///     "enabled": true,
///     "api": "openai-compat",
///     "baseUrl": "https://models.inference.ai.azure.com",
///     "apiKeyEnvVar": "GITHUB_TOKEN"
///   }
/// }
/// </code>
/// </para>
/// </remarks>
public static class GitHubModelsProvider
{
    /// <summary>The fixed inference base URL for the GitHub Models API.</summary>
    public const string BaseUrl = "https://models.inference.ai.azure.com";

    /// <summary>The provider identifier used in model registration.</summary>
    public const string ProviderName = "github-models";

    /// <summary>
    /// Compatibility overrides for GitHub Models: no <c>store</c>, no <c>developer</c> role,
    /// no reasoning effort (free-tier models).
    /// </summary>
    public static readonly OpenAICompletionsCompat Compat = new()
    {
        SupportsStore = false,
        SupportsDeveloperRole = false,
        SupportsReasoningEffort = false,
    };

    /// <summary>
    /// Registers built-in free-tier GitHub Models into <paramref name="modelRegistry"/>.
    /// All models use the <c>openai-compat</c> API so the existing provider handles
    /// requests without any additional provider registration.
    /// Authentication uses the <c>GITHUB_TOKEN</c> environment variable resolved
    /// at runtime by <see cref="EnvironmentApiKeys.GetApiKey(string)"/>.
    /// </summary>
    public static void RegisterModels(ModelRegistry modelRegistry)
    {
        Register(modelRegistry, "gpt-4o-mini",                "GPT-4o Mini",                    128000,  4096);
        Register(modelRegistry, "gpt-4o",                     "GPT-4o",                         128000,  4096);
        Register(modelRegistry, "Phi-3.5-mini-instruct",      "Phi-3.5 Mini Instruct",          128000,  4096);
        Register(modelRegistry, "Phi-4",                      "Phi-4",                          128000, 16384);
        Register(modelRegistry, "Meta-Llama-3.1-8B-Instruct", "Meta Llama 3.1 8B Instruct",    128000,  2048);
        Register(modelRegistry, "Mistral-small",              "Mistral Small",                   32000,  4096);
        Register(modelRegistry, "AI21-Jamba-1.5-Mini",        "AI21 Jamba 1.5 Mini",            256000,  4096);
    }

    private static void Register(
        ModelRegistry modelRegistry,
        string id,
        string name,
        int contextWindow,
        int maxTokens)
    {
        modelRegistry.Register(ProviderName, new LlmModel(
            Id: id,
            Name: name,
            Api: "openai-compat",
            Provider: ProviderName,
            BaseUrl: BaseUrl,
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: contextWindow,
            MaxTokens: maxTokens,
            SupportsExtraHighThinking: false,
            Headers: null,
            Compat: Compat));
    }
}
