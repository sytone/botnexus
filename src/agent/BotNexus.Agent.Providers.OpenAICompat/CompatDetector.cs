using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.OpenAICompat;

/// <summary>
/// Auto-detect compatibility settings from model baseUrl or provider name.
/// Each OpenAI-compatible server has its own quirks — this handles them.
/// </summary>
public static class CompatDetector
{
    public static OpenAICompletionsCompat Detect(LlmModel model)
    {
        // If the model already has explicit compat settings, use those
        if (model.Compat is not null)
            return model.Compat;

        var provider = model.Provider.ToLowerInvariant();
        var baseUrl = model.BaseUrl.ToLowerInvariant();

        // Ollama: localhost:11434
        if (provider == "ollama" || baseUrl.Contains("localhost:11434") || baseUrl.Contains("127.0.0.1:11434"))
        {
            return new OpenAICompletionsCompat
            {
                SupportsStore = false,
                SupportsDeveloperRole = false,
                SupportsReasoningEffort = false,
                SupportsUsageInStreaming = false,
                MaxTokensField = "max_tokens",
                SupportsStrictMode = false,
                RequiresToolResultName = true,
            };
        }

        // vLLM: typically localhost:8000
        if (provider == "vllm" || baseUrl.Contains("localhost:8000"))
        {
            return new OpenAICompletionsCompat
            {
                SupportsStore = false,
                SupportsDeveloperRole = false,
                SupportsReasoningEffort = false,
                SupportsStrictMode = false,
            };
        }

        // LM Studio: localhost:1234
        if (provider == "lmstudio" || provider == "lm-studio" || baseUrl.Contains("localhost:1234"))
        {
            return new OpenAICompletionsCompat
            {
                SupportsStore = false,
                SupportsDeveloperRole = false,
                SupportsReasoningEffort = false,
                MaxTokensField = "max_tokens",
                SupportsStrictMode = false,
            };
        }

        // SGLang
        if (provider == "sglang")
        {
            return new OpenAICompletionsCompat
            {
                SupportsStore = false,
                SupportsDeveloperRole = false,
                SupportsReasoningEffort = false,
                SupportsStrictMode = false,
            };
        }

        // Cerebras
        if (provider == "cerebras" || baseUrl.Contains("cerebras.ai"))
        {
            return new OpenAICompletionsCompat
            {
                SupportsStore = false,
                SupportsDeveloperRole = false,
                SupportsReasoningEffort = false,
            };
        }

        // xAI (Grok)
        if (provider == "xai" || baseUrl.Contains("api.x.ai"))
        {
            return new OpenAICompletionsCompat
            {
                SupportsStore = false,
                SupportsDeveloperRole = false,
                SupportsReasoningEffort = false,
            };
        }

        // DeepSeek
        if (provider == "deepseek" || baseUrl.Contains("deepseek.com"))
        {
            return new OpenAICompletionsCompat
            {
                SupportsStore = false,
                SupportsDeveloperRole = false,
                RequiresAssistantAfterToolResult = true,
            };
        }

        // Groq — mostly standard
        if (provider == "groq" || baseUrl.Contains("groq.com"))
        {
            return new OpenAICompletionsCompat
            {
                SupportsStore = false,
                SupportsDeveloperRole = false,
            };
        }

        // Default: conservative compat for unknown servers
        return new OpenAICompletionsCompat
        {
            SupportsStore = false,
            SupportsDeveloperRole = false,
            SupportsReasoningEffort = false,
            SupportsStrictMode = false,
        };
    }
}
