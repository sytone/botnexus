using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Payload sent to <c>POST /api/diagnostics/channel-error</c>.
/// Channel-agnostic: any extension can use this to report unhandled errors.
/// </summary>
public sealed class ChannelErrorReportDto
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    [JsonPropertyName("componentStack")]
    public string? ComponentStack { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }
}
