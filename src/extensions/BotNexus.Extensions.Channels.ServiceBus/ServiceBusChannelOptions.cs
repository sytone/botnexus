namespace BotNexus.Extensions.Channels.ServiceBus;

/// <summary>
/// Authentication mode resolved from <see cref="ServiceBusChannelOptions"/>.
/// </summary>
internal enum ServiceBusAuthMode
{
    /// <summary>Neither a connection string nor a fully-qualified namespace was provided.</summary>
    None,

    /// <summary>Authenticate with a Shared Access Policy connection string.</summary>
    ConnectionString,

    /// <summary>Authenticate with managed identity against a fully-qualified namespace.</summary>
    ManagedIdentity,
}

/// <summary>
/// Configuration options for the Azure Service Bus channel adapter.
/// Bind under the <c>channels:servicebus</c> configuration key, or configure via the
/// <c>AddBotNexusServiceBusChannel</c> delegate overload.
/// </summary>
public sealed class ServiceBusChannelOptions
{
    /// <summary>
    /// Service Bus connection string (e.g., from the Azure portal Shared Access Policy).
    /// Use this for simple deployments. Takes precedence over
    /// <see cref="FullyQualifiedNamespace"/> when both are set.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Fully-qualified Service Bus namespace (e.g., <c>my-namespace.servicebus.windows.net</c>).
    /// When set and <see cref="ConnectionString"/> is empty, the adapter authenticates with
    /// managed identity via <c>DefaultAzureCredential</c>. This is the recommended keyless
    /// setup and is required for namespaces created with <c>disableLocalAuth: true</c>.
    /// </summary>
    public string? FullyQualifiedNamespace { get; set; }

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
