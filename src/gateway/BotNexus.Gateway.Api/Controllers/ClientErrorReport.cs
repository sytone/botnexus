using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Payload from the portal client when a UI error is caught by the error boundary
/// or JS global error handlers.
/// </summary>
public sealed class ClientErrorReport
{
    /// <summary>Short error message.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Server-side or component stack trace.</summary>
    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    /// <summary>Blazor component stack (where available).</summary>
    [JsonPropertyName("componentStack")]
    public string? ComponentStack { get; init; }

    /// <summary>Browser URL where the error occurred.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>Browser user-agent string.</summary>
    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; init; }

    /// <summary>UTC timestamp from the client.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Active session ID if known.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>Active agent ID if known.</summary>
    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }
}
