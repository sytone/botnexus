namespace BotNexus.Providers.Core.Compatibility;

/// <summary>
/// Compatibility settings for OpenAI-compatible completions APIs.
/// Port of pi-mono's OpenAICompletionsCompat interface.
/// </summary>
public record OpenAICompletionsCompat
{
    /// <summary>
    /// Gets or sets the supports store param.
    /// </summary>
    public bool? SupportsStoreParam { get; init; } = true;
    /// <summary>
    /// Gets or sets the supports store.
    /// </summary>
    public bool? SupportsStore { get; init; } = true;
    /// <summary>
    /// Gets or sets the supports developer role.
    /// </summary>
    public bool? SupportsDeveloperRole { get; init; } = true;
    /// <summary>
    /// Gets or sets the supports temperature.
    /// </summary>
    public bool? SupportsTemperature { get; init; } = true;
    /// <summary>
    /// Gets or sets the supports metadata.
    /// </summary>
    public bool? SupportsMetadata { get; init; } = true;
    /// <summary>
    /// Gets or sets the supports reasoning effort.
    /// </summary>
    public bool? SupportsReasoningEffort { get; init; } = true;
    /// <summary>
    /// Gets or sets the reasoning effort map.
    /// </summary>
    public Dictionary<Models.ThinkingLevel, string>? ReasoningEffortMap { get; init; }
    /// <summary>
    /// Gets or sets the supports usage in streaming.
    /// </summary>
    public bool? SupportsUsageInStreaming { get; init; } = true;
    /// <summary>
    /// Gets or sets the max tokens field.
    /// </summary>
    public string MaxTokensField { get; init; } = "max_completion_tokens";
    /// <summary>
    /// Gets or sets the requires tool result name.
    /// </summary>
    public bool? RequiresToolResultName { get; init; } = false;
    /// <summary>
    /// Gets or sets the requires assistant after tool result.
    /// </summary>
    public bool? RequiresAssistantAfterToolResult { get; init; } = false;
    /// <summary>
    /// Gets or sets the requires thinking as text.
    /// </summary>
    public bool? RequiresThinkingAsText { get; init; } = false;
    /// <summary>
    /// Gets or sets the thinking format.
    /// </summary>
    public string ThinkingFormat { get; init; } = "openai";
    /// <summary>
    /// Gets or sets the open router routing.
    /// </summary>
    public OpenRouterRouting? OpenRouterRouting { get; init; } = new();
    /// <summary>
    /// Gets or sets the vercel gateway routing.
    /// </summary>
    public VercelGatewayRouting? VercelGatewayRouting { get; init; } = new();
    /// <summary>
    /// Gets or sets the zai tool stream.
    /// </summary>
    public bool? ZaiToolStream { get; init; } = false;
    /// <summary>
    /// Gets or sets the supports strict mode.
    /// </summary>
    public bool? SupportsStrictMode { get; init; } = true;
}

/// <summary>
/// Represents open router routing.
/// </summary>
public record OpenRouterRouting
{
    /// <summary>
    /// Gets or sets the only.
    /// </summary>
    public IReadOnlyList<string>? Only { get; init; }
    /// <summary>
    /// Gets or sets the order.
    /// </summary>
    public IReadOnlyList<string>? Order { get; init; }
}

/// <summary>
/// Represents vercel gateway routing.
/// </summary>
public record VercelGatewayRouting
{
    /// <summary>
    /// Gets or sets the only.
    /// </summary>
    public IReadOnlyList<string>? Only { get; init; }
    /// <summary>
    /// Gets or sets the order.
    /// </summary>
    public IReadOnlyList<string>? Order { get; init; }
}
