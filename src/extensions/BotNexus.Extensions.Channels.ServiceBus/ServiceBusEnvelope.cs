using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.ServiceBus;

/// <summary>
/// JSON envelope expected in the body of inbound Service Bus messages.
/// All fields map 1-to-1 to <see cref="BotNexus.Gateway.Abstractions.Models.InboundMessage"/> fields.
/// </summary>
public sealed class ServiceBusInboundEnvelope
{
    /// <summary>Client-assigned message identifier, used for idempotency and tracing.</summary>
    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    /// <summary>
    /// Correlation identifier linking this request to its reply.
    /// Preserved in the outbound reply envelope so the caller can match responses.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>Target agent identifier; maps to <c>InboundMessage.TargetAgentId</c>.</summary>
    [JsonPropertyName("agentId")]
    public string? AgentId { get; set; }

    /// <summary>
    /// Conversation identifier used to resume or group messages in the same thread.
    /// Maps to <c>InboundMessage.ConversationId</c> and <c>InboundMessage.ChannelAddress</c>.
    /// </summary>
    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; set; }

    /// <summary>
    /// Existing session identifier. When set, the gateway resumes the named session
    /// rather than creating a new one. Maps to <c>InboundMessage.SessionId</c>.
    /// </summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    /// <summary>Sender identifier (e.g., user email or system name). Maps to <c>InboundMessage.SenderId</c>.</summary>
    [JsonPropertyName("senderId")]
    public string? SenderId { get; set; }

    /// <summary>Message role (e.g., "user"). Informational only; the gateway treats all inbound as user messages.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>The message text content. Maps to <c>InboundMessage.Content</c>.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Reply queue name. When set, outbound replies are sent here instead of the configured
    /// <see cref="ServiceBusChannelOptions.DefaultReplyQueueName"/>.
    /// </summary>
    [JsonPropertyName("replyTo")]
    public string? ReplyTo { get; set; }

    /// <summary>When the message was created by the sender.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>
    /// Caller-supplied key/value metadata forwarded into <c>InboundMessage.Metadata</c>.
    /// Values are deserialized as <see cref="System.Text.Json.JsonElement"/> by the runtime.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; set; }
}

/// <summary>
/// JSON envelope written to the reply queue by <see cref="ServiceBusChannelAdapter.SendAsync"/>.
/// </summary>
public sealed class ServiceBusOutboundEnvelope
{
    /// <summary>Gateway-assigned reply message identifier.</summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Correlation identifier copied from the original request, allowing the caller
    /// to match this reply to its originating request.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>Agent that produced this reply, when known.</summary>
    [JsonPropertyName("agentId")]
    public string? AgentId { get; set; }

    /// <summary>Conversation identifier echoed from the request.</summary>
    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; set; }

    /// <summary>Session identifier for the agent session that handled this request.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    /// <summary>Message role; always "assistant" for gateway replies.</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    /// <summary>The agent reply text.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the reply was produced.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optional additional metadata attached to the reply.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; set; }
}
