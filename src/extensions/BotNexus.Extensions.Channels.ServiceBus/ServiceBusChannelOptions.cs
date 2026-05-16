namespace BotNexus.Extensions.Channels.ServiceBus;

/// <summary>
/// Configuration options for the Azure Service Bus channel adapter.
/// Bind under the <c>channels:servicebus</c> configuration key, or configure via the
/// <c>AddBotNexusServiceBusChannel</c> delegate overload.
/// </summary>
public sealed class ServiceBusChannelOptions
{
    /// <summary>
    /// Service Bus connection string (e.g., from the Azure portal Shared Access Policy).
    /// Use this for simple deployments. For managed-identity auth, inject a custom
    /// <see cref="IServiceBusAdapterClientFactory"/> instead.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Name of the Service Bus queue the adapter listens on for inbound messages.
    /// </summary>
    public string InboundQueueName { get; set; } = "botnexus-inbound";

    /// <summary>
    /// Default queue to send outbound replies to when the inbound envelope does not
    /// specify a <c>replyTo</c> queue name.
    /// </summary>
    public string DefaultReplyQueueName { get; set; } = "botnexus-outbound";

    /// <summary>
    /// Maximum number of messages processed concurrently by the Service Bus processor.
    /// Increasing this value allows parallel agent requests but requires each agent
    /// to be capable of concurrent operation.
    /// </summary>
    public int MaxConcurrentCalls { get; set; } = 1;

    /// <summary>
    /// Allow-list of sender IDs permitted to dispatch messages into the gateway.
    /// When non-empty, messages from senders not in this list are silently dropped.
    /// An empty list permits all senders.
    /// </summary>
    public ICollection<string> AllowedSenderIds { get; } = [];
}
