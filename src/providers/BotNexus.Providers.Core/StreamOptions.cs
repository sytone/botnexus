using BotNexus.Providers.Core.Models;

namespace BotNexus.Providers.Core;

/// <summary>
/// Base options shared by all providers. Maps to pi-mono's StreamOptions.
/// CancellationToken replaces AbortSignal from the TypeScript version.
/// </summary>
public record class StreamOptions
{
    public StreamOptions()
    {
    }

    protected StreamOptions(StreamOptions original)
    {
        Temperature = original.Temperature;
        MaxTokens = original.MaxTokens;
        CancellationToken = original.CancellationToken;
        ApiKey = original.ApiKey;
        Transport = original.Transport;
        CacheRetention = original.CacheRetention;
        SessionId = original.SessionId;
        OnPayload = original.OnPayload;
        Headers = original.Headers is null ? null : new Dictionary<string, string>(original.Headers);
        MaxRetryDelayMs = original.MaxRetryDelayMs;
        Metadata = original.Metadata is null ? null : new Dictionary<string, object>(original.Metadata);
    }

    /// <summary>
    /// Gets or sets the temperature.
    /// </summary>
    public float? Temperature { get; init; }
    /// <summary>
    /// Gets or sets the max tokens.
    /// </summary>
    public int? MaxTokens { get; init; }
    /// <summary>
    /// Gets a value indicating whether cancellation token.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }
    /// <summary>
    /// Gets or sets the api key.
    /// </summary>
    public string? ApiKey { get; init; }
    /// <summary>
    /// Gets or sets the transport.
    /// </summary>
    public Transport Transport { get; init; } = Transport.Sse;
    /// <summary>
    /// Gets or sets the cache retention.
    /// </summary>
    public CacheRetention CacheRetention { get; init; } = CacheRetention.Short;
    /// <summary>
    /// Gets or sets the session id.
    /// </summary>
    public string? SessionId { get; init; }
    /// <summary>
    /// Gets or sets the on payload.
    /// </summary>
    public Func<object, LlmModel, Task<object?>>? OnPayload { get; init; }
    /// <summary>
    /// Gets or sets the headers.
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }
    /// <summary>
    /// Gets or sets the max retry delay ms.
    /// </summary>
    public int MaxRetryDelayMs { get; init; } = 60000;
    /// <summary>
    /// Gets or sets the metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Extended options with reasoning/thinking support. Maps to pi-mono's SimpleStreamOptions.
/// </summary>
public record class SimpleStreamOptions : StreamOptions
{
    /// <summary>
    /// Gets or sets the reasoning.
    /// </summary>
    public ThinkingLevel? Reasoning { get; init; }
    /// <summary>
    /// Gets or sets the thinking budgets.
    /// </summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }
}
