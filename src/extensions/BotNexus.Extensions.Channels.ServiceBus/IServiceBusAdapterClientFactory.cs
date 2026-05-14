using Azure.Messaging.ServiceBus;

namespace BotNexus.Extensions.Channels.ServiceBus;

/// <summary>
/// Abstraction over the sealed <see cref="ServiceBusSender"/> SDK type.
/// Inject a test double to verify outbound message content and queue routing
/// without a real Azure connection.
/// </summary>
public interface IServiceBusSenderWrapper : IAsyncDisposable
{
    /// <summary>Sends a single message to the Service Bus queue this sender targets.</summary>
    Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory that creates the Service Bus processor and sender components used by
/// <see cref="ServiceBusChannelAdapter"/>.
/// </summary>
/// <remarks>
/// The default implementation wraps a real <see cref="ServiceBusClient"/> created from
/// <see cref="ServiceBusChannelOptions.ConnectionString"/>. For managed-identity or custom
/// credential scenarios, provide an alternative implementation via dependency injection
/// before calling <c>AddBotNexusServiceBusChannel</c>.
/// </remarks>
public interface IServiceBusAdapterClientFactory
{
    /// <summary>Creates a processor that receives messages from <paramref name="queueName"/>.</summary>
    ServiceBusProcessor CreateProcessor(string queueName, ServiceBusProcessorOptions options);

    /// <summary>Creates a sender that targets <paramref name="queueName"/>.</summary>
    IServiceBusSenderWrapper CreateSender(string queueName);
}
