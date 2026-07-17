using System.Text.Json.Serialization;

namespace BotNexus.TeamsProxy.Models;

/// <summary>
/// Wire-format envelope read from the BotNexus outbound Service Bus queue.
/// Matches the <c>ServiceBusOutboundEnvelope</c> shape written by the
/// BotNexus.Extensions.Channels.ServiceBus channel adapter (PR #215).
/// </summary>
public sealed class ServiceBusOutboundEnvelope
{
    /// <summary>Gateway-assigned reply message identifier.</summary>
    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    /// <summary>Correlation identifier copied from the originating inbound envelope.</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>Agent that produced this reply.</summary>
    [JsonPropertyName("agentId")]
    public string? AgentId { get; set; }

    /// <summary>
    /// Teams conversation identifier echoed from the inbound envelope.
    /// Used to look up Teams routing context in <see cref="Services.ConversationContextStore"/>.
    /// </summary>
    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; set; }

    /// <summary>BotNexus session identifier for the session that handled this request.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    /// <summary>Message role; always "assistant" for BotNexus replies.</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    /// <summary>Agent reply text to send back to the Teams conversation.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary><c>delta</c> for incremental text or <c>done</c> for the consolidated reply.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "done";

    /// <summary>Zero-based order within one response stream.</summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    /// <summary>True for the terminal consolidated response.</summary>
    [JsonPropertyName("isFinal")]
    public bool IsFinal { get; set; } = true;

    /// <summary>UTC timestamp when the reply was produced.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>Optional metadata attached to the reply.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; set; }
}
