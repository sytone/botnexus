using BotNexus.Agent.Providers.Core.Compatibility;

namespace BotNexus.Agent.Providers.Core.Models;

/// <summary>
/// Cost per million tokens for a model.
/// </summary>
public record ModelCost(
    decimal Input,
    decimal Output,
    decimal CacheRead,
    decimal CacheWrite
);

/// <summary>
/// LLM model definition. Faithful port of pi-mono's Model interface.
/// </summary>
public record LlmModel(
    string Id,
    string Name,
    string Api,
    string Provider,
    string BaseUrl,
    bool Reasoning,
    IReadOnlyList<string> Input,
    ModelCost Cost,
    int ContextWindow,
    int MaxTokens,
    bool SupportsExtraHighThinking = false,
    IReadOnlyDictionary<string, string>? Headers = null,
    OpenAICompletionsCompat? Compat = null
);
