using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

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
    [Display(
        Name = "Connection string",
        Description = "Service Bus connection string from a Shared Access Policy. Takes precedence over the fully-qualified namespace. Sensitive: stored and shown masked.",
        GroupName = "Service Bus",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "service-bus", Order = 0, Secret = true)]
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Fully-qualified Service Bus namespace (e.g., <c>my-namespace.servicebus.windows.net</c>).
    /// When set and <see cref="ConnectionString"/> is empty, the adapter authenticates with
    /// managed identity via <c>DefaultAzureCredential</c>. This is the recommended keyless
    /// setup and is required for namespaces created with <c>disableLocalAuth: true</c>.
    /// </summary>
    [Display(
        Name = "Fully-qualified namespace",
        Description = "Fully-qualified Service Bus namespace. When set and no connection string is present, the adapter uses managed identity.",
        GroupName = "Service Bus",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "service-bus", Order = 1)]
    public string? FullyQualifiedNamespace { get; set; }

    /// <summary>
    /// Name of the Service Bus queue the adapter listens on for inbound messages.
    /// </summary>
    [Display(
        Name = "Inbound queue name",
        Description = "Name of the Service Bus queue the adapter listens on for inbound messages.",
        GroupName = "Service Bus",
        Order = 2)]
    [DefaultValue("botnexus-inbound")]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "service-bus", Order = 2)]
    public string InboundQueueName { get; set; } = "botnexus-inbound";

    /// <summary>
    /// Default queue to send outbound replies to when the inbound envelope does not
    /// specify a <c>replyTo</c> queue name.
    /// </summary>
    [Display(
        Name = "Default reply queue name",
        Description = "Default queue for outbound replies when the inbound envelope does not specify a replyTo queue.",
        GroupName = "Service Bus",
        Order = 3)]
    [DefaultValue("botnexus-outbound")]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "service-bus", Order = 3)]
    public string DefaultReplyQueueName { get; set; } = "botnexus-outbound";

    /// <summary>
    /// Maximum number of messages processed concurrently by the Service Bus processor.
    /// Increasing this value allows parallel agent requests but requires each agent
    /// to be capable of concurrent operation.
    /// </summary>
    [Display(
        Name = "Max concurrent calls",
        Description = "Maximum number of messages processed concurrently by the Service Bus processor.",
        GroupName = "Service Bus",
        Order = 4)]
    [DefaultValue(1)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "service-bus", Order = 4)]
    public int MaxConcurrentCalls { get; set; } = 1;

    /// <summary>
    /// Allow-list of sender IDs permitted to dispatch messages into the gateway.
    /// When non-empty, messages from senders not in this list are silently dropped.
    /// An empty list permits all senders.
    /// </summary>
    public ICollection<string> AllowedSenderIds { get; } = [];
}
