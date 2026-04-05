namespace BotNexus.Providers.Core.Compatibility;

/// <summary>
/// Compatibility settings for OpenAI-compatible completions APIs.
/// Port of pi-mono's OpenAICompletionsCompat interface.
/// </summary>
public record OpenAICompletionsCompat
{
    public bool? SupportsStoreParam { get; init; } = true;
    public bool? SupportsStore { get; init; } = true;
    public bool? SupportsDeveloperRole { get; init; } = true;
    public bool? SupportsTemperature { get; init; } = true;
    public bool? SupportsMetadata { get; init; } = true;
    public bool? SupportsReasoningEffort { get; init; } = true;
    public Dictionary<Models.ThinkingLevel, string>? ReasoningEffortMap { get; init; }
    public bool? SupportsUsageInStreaming { get; init; } = true;
    public string MaxTokensField { get; init; } = "max_completion_tokens";
    public bool? RequiresToolResultName { get; init; } = false;
    public bool? RequiresAssistantAfterToolResult { get; init; } = false;
    public bool? RequiresThinkingAsText { get; init; } = false;
    public string ThinkingFormat { get; init; } = "openai";
    public OpenRouterRouting? OpenRouterRouting { get; init; } = new();
    public VercelGatewayRouting? VercelGatewayRouting { get; init; } = new();
    public bool? ZaiToolStream { get; init; } = false;
    public bool? SupportsStrictMode { get; init; } = true;
}

public record OpenRouterRouting
{
    public IReadOnlyList<string>? Only { get; init; }
    public IReadOnlyList<string>? Order { get; init; }
}

public record VercelGatewayRouting
{
    public IReadOnlyList<string>? Only { get; init; }
    public IReadOnlyList<string>? Order { get; init; }
}
