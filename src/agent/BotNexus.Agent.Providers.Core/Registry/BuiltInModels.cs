using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// Built-in model definitions ported from pi-mono's models.generated.ts.
/// Registered at startup by calling RegisterAll().
/// </summary>
public sealed class BuiltInModels
{
    private static readonly IReadOnlyDictionary<string, string> CopilotHeaders = new Dictionary<string, string>
    {
        ["User-Agent"] = "GitHubCopilotChat/0.35.0",
        ["Editor-Version"] = "vscode/1.107.0",
        ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
        ["Copilot-Integration-Id"] = "vscode-chat"
    };

    private static readonly OpenAICompletionsCompat CopilotCompletionsCompat = new()
    {
        SupportsStore = false,
        SupportsDeveloperRole = false,
        SupportsReasoningEffort = false
    };

    /// <summary>
    /// The individual GitHub Copilot host, used as the default when no enterprise endpoint is
    /// resolved for the account (#1639).
    /// </summary>
    private const string CopilotBaseUrl = "https://api.individual.githubcopilot.com";
    private const string AnthropicBaseUrl = "https://api.anthropic.com";
    private const string OpenAiBaseUrl = "https://api.openai.com/v1";
    private static readonly ModelCost FreeCost = new(0, 0, 0, 0);

    /// <summary>
    /// Register all built-in models with the global ModelRegistry.
    /// </summary>
    /// <param name="modelRegistry">The registry to populate.</param>
    /// <param name="endpointResolver">
    /// #1639: resolves the per-provider API endpoint (e.g. enterprise vs individual GitHub
    /// Copilot host from <c>auth.json</c>) so the Copilot models are born with the CORRECT
    /// <see cref="LlmModel.BaseUrl"/> and no consumer has to patch it afterwards. When null, or when
    /// it yields no endpoint for <c>github-copilot</c>, the individual host is used as before.
    /// </param>
    public void RegisterAll(ModelRegistry modelRegistry, Func<string, string?>? endpointResolver = null)
    {
        RegisterCopilotModels(modelRegistry, endpointResolver);
        RegisterAnthropicModels(modelRegistry);
        RegisterOpenAIModels(modelRegistry);
    }

    private static void RegisterCopilotModels(ModelRegistry modelRegistry, Func<string, string?>? endpointResolver)
    {
        // #1639: resolve the Copilot host once, at registration, so every model below is correct by
        // construction (enterprise vs individual). Falls back to the individual host when no
        // resolver is supplied or it declares no override for the provider.
        var copilotBaseUrl = ResolveCopilotBaseUrl(endpointResolver);
        Register(modelRegistry, "github-copilot", "claude-haiku-4.5", "Claude Haiku 4.5", "github-copilot-messages", copilotBaseUrl, true, ["text", "image"], 144000, 32000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "claude-opus-4.5", "Claude Opus 4.5", "github-copilot-messages", copilotBaseUrl, true, ["text", "image"], 160000, 32000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "claude-opus-4.6", "Claude Opus 4.6", "github-copilot-messages", copilotBaseUrl, true, ["text", "image"], 200000, 64000, supportsExtraHighThinking: true, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "claude-opus-4.8", "Claude Opus 4.8", "github-copilot-messages", copilotBaseUrl, true, ["text", "image"], 200000, 64000, supportsExtraHighThinking: true, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "claude-sonnet-4", "Claude Sonnet 4", "github-copilot-messages", copilotBaseUrl, true, ["text", "image"], 216000, 16000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "claude-sonnet-4.5", "Claude Sonnet 4.5", "github-copilot-messages", copilotBaseUrl, true, ["text", "image"], 144000, 32000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "claude-sonnet-4.6", "Claude Sonnet 4.6", "github-copilot-messages", copilotBaseUrl, true, ["text", "image"], 200000, 32000, headers: CopilotHeaders);

        Register(modelRegistry, "github-copilot", "gemini-2.5-pro", "Gemini 2.5 Pro", "github-copilot-completions", copilotBaseUrl, false, ["text", "image"], 128000, 64000, headers: CopilotHeaders, compat: CopilotCompletionsCompat);
        Register(modelRegistry, "github-copilot", "gemini-3-flash-preview", "Gemini 3 Flash", "github-copilot-completions", copilotBaseUrl, true, ["text", "image"], 128000, 64000, headers: CopilotHeaders, compat: CopilotCompletionsCompat);
        Register(modelRegistry, "github-copilot", "gemini-3-pro-preview", "Gemini 3 Pro Preview", "github-copilot-completions", copilotBaseUrl, true, ["text", "image"], 128000, 64000, headers: CopilotHeaders, compat: CopilotCompletionsCompat);
        Register(modelRegistry, "github-copilot", "gemini-3.1-pro-preview", "Gemini 3.1 Pro Preview", "github-copilot-completions", copilotBaseUrl, true, ["text", "image"], 128000, 64000, headers: CopilotHeaders, compat: CopilotCompletionsCompat);

        Register(modelRegistry, "github-copilot", "gpt-4.1", "GPT-4.1", "github-copilot-completions", copilotBaseUrl, false, ["text", "image"], 128000, 16384, headers: CopilotHeaders, compat: CopilotCompletionsCompat);
        Register(modelRegistry, "github-copilot", "gpt-4o", "GPT-4o", "github-copilot-completions", copilotBaseUrl, false, ["text", "image"], 128000, 4096, headers: CopilotHeaders, compat: CopilotCompletionsCompat);

        Register(modelRegistry, "github-copilot", "gpt-5", "GPT-5", "github-copilot-responses", copilotBaseUrl, true, ["text", "image"], 128000, 128000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5-mini", "GPT-5-mini", "github-copilot-responses", copilotBaseUrl, true, ["text", "image"], 264000, 64000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.1", "GPT-5.1", "github-copilot-responses", copilotBaseUrl, true, ["text", "image"], 264000, 64000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.1-codex", "GPT-5.1-Codex", "github-copilot-responses", copilotBaseUrl, true, ["text", "image"], 400000, 128000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.1-codex-max", "GPT-5.1-Codex-max", "github-copilot-responses", copilotBaseUrl, true, ["text", "image"], 400000, 128000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.1-codex-mini", "GPT-5.1-Codex-mini", "github-copilot-responses", copilotBaseUrl, true, ["text", "image"], 400000, 128000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.2", "GPT-5.2", "github-copilot-responses", copilotBaseUrl, true, ["text", "image"], 264000, 64000, supportsExtraHighThinking: true, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.2-codex", "GPT-5.2-Codex", "github-copilot-responses", copilotBaseUrl, true, ["text", "image"], 400000, 128000, supportsExtraHighThinking: true, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.3-codex", "GPT-5.3-Codex", "github-copilot-responses", copilotBaseUrl, true, ["text", "image"], 400000, 128000, supportsExtraHighThinking: true, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.4", "GPT-5.4", "github-copilot-responses", copilotBaseUrl, true, ["text", "image"], 400000, 128000, supportsExtraHighThinking: true, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.4-mini", "GPT-5.4 mini", "github-copilot-responses", copilotBaseUrl, true, ["text", "image"], 400000, 128000, supportsExtraHighThinking: true, headers: CopilotHeaders);

        Register(modelRegistry, "github-copilot", "grok-code-fast-1", "Grok Code Fast 1", "github-copilot-completions", copilotBaseUrl, true, ["text"], 128000, 64000, headers: CopilotHeaders, compat: CopilotCompletionsCompat);
    }

    /// <summary>
    /// Resolves the Copilot host to stamp onto every registered Copilot model (#1639). Prefers the
    /// per-provider endpoint from <paramref name="endpointResolver"/> (enterprise account), falling
    /// back to the individual host when none is declared. Pure and null-safe.
    /// </summary>
    private static string ResolveCopilotBaseUrl(Func<string, string?>? endpointResolver)
    {
        var resolved = endpointResolver?.Invoke("github-copilot");
        return string.IsNullOrWhiteSpace(resolved) ? CopilotBaseUrl : resolved;
    }

    private static void RegisterAnthropicModels(ModelRegistry modelRegistry)
    {
        Register(modelRegistry, "anthropic", "claude-3-5-haiku-20241022", "Claude Haiku 3.5", "anthropic-messages", AnthropicBaseUrl, false, ["text", "image"], 200000, 8192);
        Register(modelRegistry, "anthropic", "claude-sonnet-4-20250514", "Claude Sonnet 4", "anthropic-messages", AnthropicBaseUrl, true, ["text", "image"], 200000, 64000, supportsExtendedContextWindow: true);
        Register(modelRegistry, "anthropic", "claude-sonnet-4-5-20250929", "Claude Sonnet 4.5", "anthropic-messages", AnthropicBaseUrl, true, ["text", "image"], 200000, 64000, supportsExtendedContextWindow: true);
        Register(modelRegistry, "anthropic", "claude-opus-4-5-20250929", "Claude Opus 4.5", "anthropic-messages", AnthropicBaseUrl, true, ["text", "image"], 200000, 64000, supportsExtendedContextWindow: true);
    }

    private static void RegisterOpenAIModels(ModelRegistry modelRegistry)
    {
        Register(modelRegistry, "openai", "gpt-4.1", "GPT-4.1", "openai-completions", OpenAiBaseUrl, false, ["text", "image"], 1047576, 32768);
        Register(modelRegistry, "openai", "gpt-4.1-mini", "GPT-4.1 Mini", "openai-completions", OpenAiBaseUrl, false, ["text", "image"], 1047576, 32768);
        Register(modelRegistry, "openai", "gpt-4o", "GPT-4o", "openai-completions", OpenAiBaseUrl, false, ["text", "image"], 128000, 16384);
        Register(modelRegistry, "openai", "o3", "o3", "openai-responses", OpenAiBaseUrl, true, ["text", "image"], 200000, 100000);
        Register(modelRegistry, "openai", "o4-mini", "o4-mini", "openai-responses", OpenAiBaseUrl, true, ["text", "image"], 200000, 100000);
    }

    private static void Register(
        ModelRegistry modelRegistry,
        string provider,
        string id,
        string name,
        string api,
        string baseUrl,
        bool reasoning,
        IReadOnlyList<string> input,
        int contextWindow,
        int maxTokens,
        bool supportsExtraHighThinking = false,
        bool supportsExtendedContextWindow = false,
        IReadOnlyDictionary<string, string>? headers = null,
        OpenAICompletionsCompat? compat = null)
    {
        modelRegistry.Register(provider, new LlmModel(
            Id: id,
            Name: name,
            Api: api,
            Provider: provider,
            BaseUrl: baseUrl,
            Reasoning: reasoning,
            Input: input,
            Cost: FreeCost,
            ContextWindow: contextWindow,
            MaxTokens: maxTokens,
            SupportsExtraHighThinking: supportsExtraHighThinking,
            SupportsExtendedContextWindow: supportsExtendedContextWindow,
            Headers: headers,
            Compat: compat));
    }
}
