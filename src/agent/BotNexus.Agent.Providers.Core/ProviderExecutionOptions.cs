using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core;

/// <summary>Provider-owned policy for credentials, wire setup, and transport execution.</summary>
public record class ProviderExecutionOptions
{
    /// <summary>Credential supplied to the provider; null permits provider ambient resolution.</summary>
    public string? ApiKey { get; init; }
    /// <summary>Wire transport selected for the provider.</summary>
    public Transport Transport { get; init; } = Transport.Sse;
    /// <summary>Additional provider request headers.</summary>
    public Dictionary<string, string>? Headers { get; init; }
    /// <summary>Provider payload customization hook.</summary>
    public Func<object, LlmModel, Task<object?>>? OnPayload { get; init; }
    /// <summary>Provider-specific execution metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
    /// <summary>Maximum provider-internal retry delay in milliseconds.</summary>
    public int MaxRetryDelayMs { get; init; } = 60_000;
    /// <summary>First-token setup timeout in milliseconds; zero disables it.</summary>
    public int StreamSetupTimeoutMs { get; init; }
    /// <summary>Maximum idle interval between response-body reads; null uses provider defaults.</summary>
    public int? StreamIdleTimeoutMs { get; init; }
}
