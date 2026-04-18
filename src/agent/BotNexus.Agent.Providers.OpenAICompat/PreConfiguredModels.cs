using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.OpenAICompat;

/// <summary>
/// Pre-configured model definitions for common local/remote OpenAI-compatible servers.
/// </summary>
public static class PreConfiguredModels
{
    public static LlmModel Ollama(string modelId, int contextWindow = 128000, int maxTokens = 32000) => new(
        Id: modelId,
        Name: $"{modelId} (Ollama)",
        Api: "openai-compat",
        Provider: "ollama",
        BaseUrl: "http://localhost:11434/v1",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: contextWindow,
        MaxTokens: maxTokens,
        Compat: new OpenAICompletionsCompat
        {
            SupportsDeveloperRole = false,
            SupportsReasoningEffort = false,
            SupportsStore = false,
            SupportsUsageInStreaming = false,
            MaxTokensField = "max_tokens",
            SupportsStrictMode = false,
            RequiresToolResultName = true,
        }
    );

    public static LlmModel VLlm(
        string modelId,
        string baseUrl = "http://localhost:8000/v1",
        int contextWindow = 128000,
        int maxTokens = 32000) => new(
        Id: modelId,
        Name: $"{modelId} (vLLM)",
        Api: "openai-compat",
        Provider: "vllm",
        BaseUrl: baseUrl,
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: contextWindow,
        MaxTokens: maxTokens,
        Compat: new OpenAICompletionsCompat
        {
            SupportsDeveloperRole = false,
            SupportsReasoningEffort = false,
            SupportsStore = false,
            SupportsStrictMode = false,
        }
    );

    public static LlmModel LMStudio(
        string modelId,
        string baseUrl = "http://localhost:1234/v1",
        int contextWindow = 128000,
        int maxTokens = 32000) => new(
        Id: modelId,
        Name: $"{modelId} (LM Studio)",
        Api: "openai-compat",
        Provider: "lmstudio",
        BaseUrl: baseUrl,
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: contextWindow,
        MaxTokens: maxTokens,
        Compat: new OpenAICompletionsCompat
        {
            SupportsDeveloperRole = false,
            SupportsReasoningEffort = false,
            SupportsStore = false,
            MaxTokensField = "max_tokens",
            SupportsStrictMode = false,
        }
    );

    public static LlmModel SGLang(
        string modelId,
        string baseUrl = "http://localhost:30000/v1",
        int contextWindow = 128000,
        int maxTokens = 32000) => new(
        Id: modelId,
        Name: $"{modelId} (SGLang)",
        Api: "openai-compat",
        Provider: "sglang",
        BaseUrl: baseUrl,
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: contextWindow,
        MaxTokens: maxTokens,
        Compat: new OpenAICompletionsCompat
        {
            SupportsDeveloperRole = false,
            SupportsReasoningEffort = false,
            SupportsStore = false,
            SupportsStrictMode = false,
        }
    );
}
