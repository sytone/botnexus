using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Tests.Compatibility;

/// <summary>
/// Tests for <see cref="CompatResolver"/>, the shared model-compatibility resolver
/// extracted from the duplicated OpenAI/Copilot Completions providers (#1406).
/// Verifies vendor detection by provider name and base URL, the per-model special
/// cases (groq qwen3-32b, qwen thinking formats), and the model-supplied override merge.
/// </summary>
public class CompatResolverTests
{
    private static LlmModel Model(
        string provider,
        string baseUrl,
        string id = "some-model",
        OpenAICompletionsCompat? compat = null)
        => new(
            Id: id,
            Name: id,
            Api: "openai-completions",
            Provider: provider,
            BaseUrl: baseUrl,
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 4096,
            MaxTokens: 1024,
            Compat: compat);

    [Fact]
    public void Resolve_UnknownVendor_ReturnsDefaults()
    {
        var compat = CompatResolver.Resolve(Model("openai", "https://api.openai.com"));

        compat.SupportsStore.ShouldBe(true);
        compat.SupportsStoreParam.ShouldBe(true);
        compat.SupportsDeveloperRole.ShouldBe(true);
        compat.SupportsTemperature.ShouldBe(true);
        compat.SupportsMetadata.ShouldBe(true);
        compat.SupportsReasoningEffort.ShouldBe(true);
        compat.SupportsUsageInStreaming.ShouldBe(true);
        compat.MaxTokensField.ShouldBe("max_completion_tokens");
        compat.ThinkingFormat.ShouldBe("openai");
        compat.SupportsStrictMode.ShouldBe(true);
        compat.ReasoningEffortMap.ShouldBeNull();
    }

    [Fact]
    public void Resolve_Cerebras_ByProvider_DisablesStoreAndDeveloperRole()
    {
        var compat = CompatResolver.Resolve(Model("cerebras", "https://example.com"));

        compat.SupportsStore.ShouldBe(false);
        compat.SupportsStoreParam.ShouldBe(false);
        compat.SupportsDeveloperRole.ShouldBe(false);
        compat.SupportsMetadata.ShouldBe(false);
        compat.MaxTokensField.ShouldBe("max_completion_tokens");
    }

    [Fact]
    public void Resolve_Cerebras_ByBaseUrl_DisablesStoreAndDeveloperRole()
    {
        var compat = CompatResolver.Resolve(Model("custom", "https://api.cerebras.ai/v1"));

        compat.SupportsStore.ShouldBe(false);
        compat.SupportsDeveloperRole.ShouldBe(false);
    }

    [Fact]
    public void Resolve_Xai_AlsoDisablesReasoningEffort()
    {
        var compat = CompatResolver.Resolve(Model("xai", "https://example.com"));

        compat.SupportsStore.ShouldBe(false);
        compat.SupportsReasoningEffort.ShouldBe(false);
    }

    [Fact]
    public void Resolve_Zai_SetsThinkingFormatZai()
    {
        var compat = CompatResolver.Resolve(Model("zai", "https://example.com"));

        compat.SupportsReasoningEffort.ShouldBe(false);
        compat.ThinkingFormat.ShouldBe("zai");
    }

    [Fact]
    public void Resolve_Chutes_ByBaseUrl_SetsMaxTokensField()
    {
        var compat = CompatResolver.Resolve(Model("custom", "https://llm.chutes.ai/v1"));

        compat.SupportsStore.ShouldBe(false);
        compat.MaxTokensField.ShouldBe("max_tokens");
    }

    [Fact]
    public void Resolve_OpenRouter_SetsThinkingFormatOpenRouter()
    {
        var compat = CompatResolver.Resolve(Model("openrouter", "https://openrouter.ai/api/v1"));

        compat.ThinkingFormat.ShouldBe("openrouter");
        // openrouter profile only changes ThinkingFormat; store flags stay default.
        compat.SupportsStore.ShouldBe(true);
    }

    [Fact]
    public void Resolve_GroqQwen3_32b_AssignsDefaultReasoningEffortMap()
    {
        var compat = CompatResolver.Resolve(Model("groq", "https://api.groq.com", id: "qwen/qwen3-32b"));

        compat.SupportsStore.ShouldBe(false);
        compat.ReasoningEffortMap.ShouldNotBeNull();
        compat.ReasoningEffortMap![ThinkingLevel.Minimal].ShouldBe("default");
        compat.ReasoningEffortMap[ThinkingLevel.High].ShouldBe("default");
        // qwen/ prefix also forces qwen thinking format unless overridden by the qwen3-32b map only.
        compat.ThinkingFormat.ShouldBe("qwen");
    }

    [Fact]
    public void Resolve_QwenSlashModel_SetsQwenThinkingFormat()
    {
        var compat = CompatResolver.Resolve(Model("custom", "https://example.com", id: "qwen/qwen2.5-7b"));

        compat.ThinkingFormat.ShouldBe("qwen");
    }

    [Fact]
    public void Resolve_QwenChatTemplateModel_SetsQwenChatTemplateThinkingFormat()
    {
        var compat = CompatResolver.Resolve(Model("custom", "https://example.com", id: "my-qwen-chat-template-model"));

        compat.ThinkingFormat.ShouldBe("qwen-chat-template");
    }

    [Fact]
    public void Resolve_ModelCompatOverride_TakesPrecedenceOverDetected()
    {
        // cerebras detection would set SupportsStore=false; an explicit override flips it back to true.
        var configured = new OpenAICompletionsCompat
        {
            SupportsStore = true,
            SupportsDeveloperRole = true,
            ThinkingFormat = "custom-format",
            MaxTokensField = "custom_tokens"
        };

        var compat = CompatResolver.Resolve(Model("cerebras", "https://example.com", compat: configured));

        compat.SupportsStore.ShouldBe(true);
        compat.SupportsDeveloperRole.ShouldBe(true);
        compat.ThinkingFormat.ShouldBe("custom-format");
        compat.MaxTokensField.ShouldBe("custom_tokens");
    }

    [Fact]
    public void Resolve_ModelCompatOverride_NullFieldsFallBackToDetected()
    {
        // An override that only sets ThinkingFormat must leave the cerebras-detected store flags intact.
        var configured = new OpenAICompletionsCompat
        {
            SupportsStore = null,
            SupportsDeveloperRole = null,
            ThinkingFormat = "only-format"
        };

        var compat = CompatResolver.Resolve(Model("cerebras", "https://example.com", compat: configured));

        compat.SupportsStore.ShouldBe(false);
        compat.SupportsDeveloperRole.ShouldBe(false);
        compat.ThinkingFormat.ShouldBe("only-format");
    }
}
