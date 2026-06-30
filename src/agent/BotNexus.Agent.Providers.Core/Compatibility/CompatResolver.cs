using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Compatibility;

/// <summary>
/// Resolves <see cref="OpenAICompletionsCompat"/> settings for an OpenAI-compatible
/// Chat Completions model.
/// </summary>
/// <remarks>
/// This logic was previously duplicated verbatim inside both
/// <c>OpenAICompletionsProvider</c> and <c>CopilotCompletionsProvider</c>. Centralising it
/// here is an Open/Closed win: a new vendor quirk becomes a single edit in
/// <see cref="CompatProfiles"/> instead of two hand-applied copies that can silently drift.
/// Behaviour is intentionally identical to the former per-provider <c>GetCompat</c>; the
/// Copilot/OpenAI parity tests assert the two providers still emit byte-identical request
/// bodies after this extraction.
/// </remarks>
public static class CompatResolver
{
    /// <summary>
    /// Per-vendor compatibility profiles keyed by the detected vendor token.
    /// Each action mutates a fresh <see cref="CompatFlags"/> in place.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Action<CompatFlags>> CompatProfiles = new Dictionary<string, Action<CompatFlags>>
    {
        ["cerebras"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
        },
        ["xai"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
            flags.SupportsReasoningEffort = false;
        },
        ["zai"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
            flags.SupportsReasoningEffort = false;
            flags.ThinkingFormat = "zai";
        },
        ["deepseek"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
        },
        ["chutes"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
            flags.MaxTokensField = "max_tokens";
        },
        ["groq"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
        },
        ["openrouter"] = flags => flags.ThinkingFormat = "openrouter"
    };

    /// <summary>
    /// Resolves the effective compatibility settings for <paramref name="model"/>, layering
    /// any explicit <see cref="LlmModel.Compat"/> overrides on top of the auto-detected vendor
    /// profile (overrides win where set; otherwise the detected value is used).
    /// </summary>
    /// <param name="model">The model whose compatibility profile should be resolved.</param>
    /// <returns>The merged <see cref="OpenAICompletionsCompat"/> to apply when building requests.</returns>
    public static OpenAICompletionsCompat Resolve(LlmModel model)
    {
        var detected = Detect(model);
        var configured = model.Compat;
        if (configured is null)
            return detected;

        return detected with
        {
            SupportsStoreParam = configured.SupportsStoreParam ?? detected.SupportsStoreParam,
            SupportsStore = configured.SupportsStore ?? detected.SupportsStore,
            SupportsDeveloperRole = configured.SupportsDeveloperRole ?? detected.SupportsDeveloperRole,
            SupportsTemperature = configured.SupportsTemperature ?? detected.SupportsTemperature,
            SupportsMetadata = configured.SupportsMetadata ?? detected.SupportsMetadata,
            SupportsReasoningEffort = configured.SupportsReasoningEffort ?? detected.SupportsReasoningEffort,
            ReasoningEffortMap = configured.ReasoningEffortMap ?? detected.ReasoningEffortMap,
            SupportsUsageInStreaming = configured.SupportsUsageInStreaming ?? detected.SupportsUsageInStreaming,
            MaxTokensField = configured.MaxTokensField,
            RequiresToolResultName = configured.RequiresToolResultName ?? detected.RequiresToolResultName,
            RequiresAssistantAfterToolResult = configured.RequiresAssistantAfterToolResult ?? detected.RequiresAssistantAfterToolResult,
            RequiresThinkingAsText = configured.RequiresThinkingAsText ?? detected.RequiresThinkingAsText,
            ThinkingFormat = configured.ThinkingFormat,
            OpenRouterRouting = configured.OpenRouterRouting ?? detected.OpenRouterRouting,
            VercelGatewayRouting = configured.VercelGatewayRouting ?? detected.VercelGatewayRouting,
            ZaiToolStream = configured.ZaiToolStream ?? detected.ZaiToolStream,
            SupportsStrictMode = configured.SupportsStrictMode ?? detected.SupportsStrictMode
        };
    }

    private static OpenAICompletionsCompat Detect(LlmModel model)
    {
        var provider = model.Provider.ToLowerInvariant();
        var baseUrl = model.BaseUrl.ToLowerInvariant();
        var flags = new CompatFlags();

        var matches = new Dictionary<string, bool>
        {
            ["cerebras"] = provider == "cerebras" || baseUrl.Contains("cerebras.ai"),
            ["xai"] = provider == "xai" || baseUrl.Contains("api.x.ai"),
            ["zai"] = provider == "zai" || baseUrl.Contains("api.z.ai"),
            ["deepseek"] = provider == "deepseek" || baseUrl.Contains("deepseek.com"),
            ["chutes"] = baseUrl.Contains("chutes.ai"),
            ["groq"] = provider == "groq" || baseUrl.Contains("groq.com"),
            ["openrouter"] = provider == "openrouter" || baseUrl.Contains("openrouter.ai")
        };

        foreach (var (key, isMatch) in matches)
        {
            if (!isMatch) continue;
            CompatProfiles[key](flags);
        }

        if (matches["groq"] && string.Equals(model.Id, "qwen/qwen3-32b", StringComparison.Ordinal))
        {
            flags.ReasoningEffortMap = new Dictionary<ThinkingLevel, string>
            {
                [ThinkingLevel.Minimal] = "default",
                [ThinkingLevel.Low] = "default",
                [ThinkingLevel.Medium] = "default",
                [ThinkingLevel.High] = "default",
                [ThinkingLevel.ExtraHigh] = "default",
                [ThinkingLevel.Max] = "default"
            };
        }

        if (model.Id.Contains("qwen-chat-template", StringComparison.OrdinalIgnoreCase))
            flags.ThinkingFormat = "qwen-chat-template";
        else if (model.Id.StartsWith("qwen/", StringComparison.OrdinalIgnoreCase))
            flags.ThinkingFormat = "qwen";

        return new OpenAICompletionsCompat
        {
            SupportsStoreParam = flags.SupportsStoreParam,
            SupportsStore = flags.SupportsStore,
            SupportsDeveloperRole = flags.SupportsDeveloperRole,
            SupportsTemperature = flags.SupportsTemperature,
            SupportsMetadata = flags.SupportsMetadata,
            SupportsReasoningEffort = flags.SupportsReasoningEffort,
            ReasoningEffortMap = flags.ReasoningEffortMap,
            SupportsUsageInStreaming = true,
            MaxTokensField = flags.MaxTokensField,
            RequiresToolResultName = false,
            RequiresAssistantAfterToolResult = false,
            RequiresThinkingAsText = false,
            ThinkingFormat = flags.ThinkingFormat,
            OpenRouterRouting = new(),
            VercelGatewayRouting = new(),
            ZaiToolStream = false,
            SupportsStrictMode = true
        };
    }

    /// <summary>
    /// Mutable accumulator used while applying vendor profiles. Defaults match the OpenAI
    /// baseline; profiles toggle individual flags before the immutable
    /// <see cref="OpenAICompletionsCompat"/> result is produced.
    /// </summary>
    private sealed class CompatFlags
    {
        public bool SupportsStoreParam { get; set; } = true;
        public bool SupportsStore { get; set; } = true;
        public bool SupportsDeveloperRole { get; set; } = true;
        public bool SupportsTemperature { get; set; } = true;
        public bool SupportsMetadata { get; set; } = true;
        public bool SupportsReasoningEffort { get; set; } = true;
        public string MaxTokensField { get; set; } = "max_completion_tokens";
        public string ThinkingFormat { get; set; } = "openai";
        public Dictionary<ThinkingLevel, string>? ReasoningEffortMap { get; set; }
    }
}
