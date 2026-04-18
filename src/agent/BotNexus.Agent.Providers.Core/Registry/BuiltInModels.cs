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

    private const string CopilotBaseUrl = "https://api.individual.githubcopilot.com";
    private const string AnthropicBaseUrl = "https://api.anthropic.com";
    private const string OpenAiBaseUrl = "https://api.openai.com/v1";
    private static readonly ModelCost FreeCost = new(0, 0, 0, 0);

    /// <summary>Register all built-in models with the global ModelRegistry.</summary>
    public void RegisterAll(ModelRegistry modelRegistry)
    {
        RegisterCopilotModels(modelRegistry);
        RegisterAnthropicModels(modelRegistry);
        RegisterOpenAIModels(modelRegistry);
    }

    private static void RegisterCopilotModels(ModelRegistry modelRegistry)
    {
        Register(modelRegistry, "github-copilot", "claude-haiku-4.5", "Claude Haiku 4.5", "anthropic-messages", CopilotBaseUrl, true, ["text", "image"], 144000, 32000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "claude-opus-4.5", "Claude Opus 4.5", "anthropic-messages", CopilotBaseUrl, true, ["text", "image"], 160000, 32000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "claude-opus-4.6", "Claude Opus 4.6", "anthropic-messages", CopilotBaseUrl, true, ["text", "image"], 1000000, 64000, supportsExtraHighThinking: true, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "claude-sonnet-4", "Claude Sonnet 4", "anthropic-messages", CopilotBaseUrl, true, ["text", "image"], 216000, 16000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "claude-sonnet-4.5", "Claude Sonnet 4.5", "anthropic-messages", CopilotBaseUrl, true, ["text", "image"], 144000, 32000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "claude-sonnet-4.6", "Claude Sonnet 4.6", "anthropic-messages", CopilotBaseUrl, true, ["text", "image"], 1000000, 32000, headers: CopilotHeaders);

        Register(modelRegistry, "github-copilot", "gemini-2.5-pro", "Gemini 2.5 Pro", "openai-completions", CopilotBaseUrl, false, ["text", "image"], 128000, 64000, headers: CopilotHeaders, compat: CopilotCompletionsCompat);
        Register(modelRegistry, "github-copilot", "gemini-3-flash-preview", "Gemini 3 Flash", "openai-completions", CopilotBaseUrl, true, ["text", "image"], 128000, 64000, headers: CopilotHeaders, compat: CopilotCompletionsCompat);
        Register(modelRegistry, "github-copilot", "gemini-3-pro-preview", "Gemini 3 Pro Preview", "openai-completions", CopilotBaseUrl, true, ["text", "image"], 128000, 64000, headers: CopilotHeaders, compat: CopilotCompletionsCompat);
        Register(modelRegistry, "github-copilot", "gemini-3.1-pro-preview", "Gemini 3.1 Pro Preview", "openai-completions", CopilotBaseUrl, true, ["text", "image"], 128000, 64000, headers: CopilotHeaders, compat: CopilotCompletionsCompat);

        Register(modelRegistry, "github-copilot", "gpt-4.1", "GPT-4.1", "openai-completions", CopilotBaseUrl, false, ["text", "image"], 128000, 16384, headers: CopilotHeaders, compat: CopilotCompletionsCompat);
        Register(modelRegistry, "github-copilot", "gpt-4o", "GPT-4o", "openai-completions", CopilotBaseUrl, false, ["text", "image"], 128000, 4096, headers: CopilotHeaders, compat: CopilotCompletionsCompat);

        Register(modelRegistry, "github-copilot", "gpt-5", "GPT-5", "openai-responses", CopilotBaseUrl, true, ["text", "image"], 128000, 128000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5-mini", "GPT-5-mini", "openai-responses", CopilotBaseUrl, true, ["text", "image"], 264000, 64000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.1", "GPT-5.1", "openai-responses", CopilotBaseUrl, true, ["text", "image"], 264000, 64000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.1-codex", "GPT-5.1-Codex", "openai-responses", CopilotBaseUrl, true, ["text", "image"], 400000, 128000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.1-codex-max", "GPT-5.1-Codex-max", "openai-responses", CopilotBaseUrl, true, ["text", "image"], 400000, 128000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.1-codex-mini", "GPT-5.1-Codex-mini", "openai-responses", CopilotBaseUrl, true, ["text", "image"], 400000, 128000, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.2", "GPT-5.2", "openai-responses", CopilotBaseUrl, true, ["text", "image"], 264000, 64000, supportsExtraHighThinking: true, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.2-codex", "GPT-5.2-Codex", "openai-responses", CopilotBaseUrl, true, ["text", "image"], 400000, 128000, supportsExtraHighThinking: true, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.3-codex", "GPT-5.3-Codex", "openai-responses", CopilotBaseUrl, true, ["text", "image"], 400000, 128000, supportsExtraHighThinking: true, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.4", "GPT-5.4", "openai-responses", CopilotBaseUrl, true, ["text", "image"], 400000, 128000, supportsExtraHighThinking: true, headers: CopilotHeaders);
        Register(modelRegistry, "github-copilot", "gpt-5.4-mini", "GPT-5.4 mini", "openai-responses", CopilotBaseUrl, true, ["text", "image"], 400000, 128000, supportsExtraHighThinking: true, headers: CopilotHeaders);

        Register(modelRegistry, "github-copilot", "grok-code-fast-1", "Grok Code Fast 1", "openai-completions", CopilotBaseUrl, true, ["text"], 128000, 64000, headers: CopilotHeaders, compat: CopilotCompletionsCompat);
    }

    private static void RegisterAnthropicModels(ModelRegistry modelRegistry)
    {
        Register(modelRegistry, "anthropic", "claude-3-5-haiku-20241022", "Claude Haiku 3.5", "anthropic-messages", AnthropicBaseUrl, false, ["text", "image"], 200000, 8192);
        Register(modelRegistry, "anthropic", "claude-sonnet-4-20250514", "Claude Sonnet 4", "anthropic-messages", AnthropicBaseUrl, true, ["text", "image"], 200000, 64000);
        Register(modelRegistry, "anthropic", "claude-sonnet-4-5-20250929", "Claude Sonnet 4.5", "anthropic-messages", AnthropicBaseUrl, true, ["text", "image"], 200000, 64000);
        Register(modelRegistry, "anthropic", "claude-opus-4-5-20250929", "Claude Opus 4.5", "anthropic-messages", AnthropicBaseUrl, true, ["text", "image"], 200000, 64000);
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
            Headers: headers,
            Compat: compat));
    }
}
