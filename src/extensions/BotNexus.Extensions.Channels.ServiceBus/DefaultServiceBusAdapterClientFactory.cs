using Azure.Messaging.ServiceBus;

namespace BotNexus.Extensions.Channels.ServiceBus;

/// <summary>
/// Production <see cref="IServiceBusAdapterClientFactory"/> implementation backed by a real
/// <see cref="ServiceBusClient"/>. Created by <see cref="ServiceBusChannelAdapter"/> when no
/// factory is injected via the constructor.
/// </summary>
internal sealed class DefaultServiceBusAdapterClientFactory : IServiceBusAdapterClientFactory, IAsyncDisposable
{
    private readonly ServiceBusClient _client;

    internal DefaultServiceBusAdapterClientFactory(ServiceBusClient client) => _client = client;

    /// <inheritdoc/>
    public ServiceBusProcessor CreateProcessor(string queueName, ServiceBusProcessorOptions options)
        => _client.CreateProcessor(queueName, options);

    /// <inheritdoc/>
    public IServiceBusSenderWrapper CreateSender(string queueName)
        => new DefaultServiceBusSenderWrapper(_client.CreateSender(queueName));

    /// <summary>Disposes the underlying <see cref="ServiceBusClient"/>.</summary>
    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}

/// <summary>
/// Production <see cref="IServiceBusSenderWrapper"/> backed by a real <see cref="ServiceBusSender"/>.
/// </summary>
internal sealed class DefaultServiceBusSenderWrapper(ServiceBusSender sender) : IServiceBusSenderWrapper
{
    /// <inheritdoc/>
    public Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        => sender.SendMessageAsync(message, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await sender.DisposeAsync();
}
