using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core;

/// <summary>Provider-private options retained for direct provider APIs and wire mapping.</summary>
public record class StreamOptions : GenerationOptions
{
    public StreamOptions() { }

    protected StreamOptions(StreamOptions original) : base(original)
    {
        ApiKey = original.ApiKey;
        Transport = original.Transport;
        OnPayload = original.OnPayload;
        Headers = original.Headers is null ? null : new Dictionary<string, string>(original.Headers);
        MaxRetryDelayMs = original.MaxRetryDelayMs;
        Metadata = original.Metadata is null ? null : new Dictionary<string, object>(original.Metadata);
        StreamSetupTimeoutMs = original.StreamSetupTimeoutMs;
        StreamIdleTimeoutMs = original.StreamIdleTimeoutMs;
    }

    /// <summary>Provider credential; null permits ambient resolution.</summary>
    public string? ApiKey { get; init; }
    /// <summary>Provider wire transport.</summary>
    public Transport Transport { get; init; } = Transport.Sse;
    /// <summary>Provider payload customization hook.</summary>
    public Func<object, LlmModel, Task<object?>>? OnPayload { get; init; }
    /// <summary>Additional provider request headers.</summary>
    public Dictionary<string, string>? Headers { get; init; }
    /// <summary>Legacy provider retry-delay setting retained for direct APIs.</summary>
    public int MaxRetryDelayMs { get; init; } = 60000;
    /// <summary>Provider-specific execution metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
    /// <summary>First-token setup timeout in milliseconds; zero disables it.</summary>
    public int StreamSetupTimeoutMs { get; init; }
    /// <summary>Maximum idle interval between streaming reads.</summary>
    public int? StreamIdleTimeoutMs { get; init; }
}

/// <summary>Provider-private direct-call options with semantic reasoning controls inherited.</summary>
public record class SimpleStreamOptions : StreamOptions;
