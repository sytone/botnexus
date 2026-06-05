using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Generic payload from a channel extension reporting an unhandled error.
/// Portal, Telegram, and other channel adapters can post to
/// <c>POST /api/diagnostics/channel-error</c> using this structure.
/// </summary>
public sealed class ChannelErrorReport
{
    /// <summary>Short error message.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Stack trace from the error.</summary>
    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    /// <summary>Optional component or render stack (e.g. Blazor component tree).</summary>
    [JsonPropertyName("componentStack")]
    public string? ComponentStack { get; init; }

    /// <summary>URL or context identifier where the error occurred.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>User-agent or channel identifier string.</summary>
    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; init; }

    /// <summary>UTC timestamp from the reporting channel.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Active session ID if known.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>Active agent ID if known.</summary>
    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }
}
