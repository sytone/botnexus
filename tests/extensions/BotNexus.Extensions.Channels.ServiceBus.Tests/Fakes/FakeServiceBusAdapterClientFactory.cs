using Azure.Messaging.ServiceBus;
using BotNexus.Extensions.Channels.ServiceBus;

namespace BotNexus.Extensions.Channels.ServiceBus.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IServiceBusAdapterClientFactory"/> for unit tests.
/// Returns a <see cref="FakeServiceBusProcessor"/> and <see cref="FakeServiceBusSenderWrapper"/>
/// instances that record calls without connecting to Azure.
/// </summary>
internal sealed class FakeServiceBusAdapterClientFactory : IServiceBusAdapterClientFactory
{
    /// <summary>The fake processor returned by <see cref="CreateProcessor"/>.</summary>
    public FakeServiceBusProcessor Processor { get; } = new();

    /// <summary>When set, the next send fails before the message is recorded.</summary>
    public bool FailNextSend { get; set; }

    /// <summary>Senders created so far, keyed by queue name.</summary>
    public Dictionary<string, FakeServiceBusSenderWrapper> Senders { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public ServiceBusProcessor CreateProcessor(string queueName, ServiceBusProcessorOptions options)
        => Processor;

    /// <inheritdoc/>
    public IServiceBusSenderWrapper CreateSender(string queueName)
    {
        var sender = new FakeServiceBusSenderWrapper(queueName, this);
        Senders[queueName] = sender;
        return sender;
    }
}

/// <summary>
/// Fake <see cref="ServiceBusProcessor"/> that records lifecycle calls without
/// connecting to Azure. Uses the protected parameterless constructor provided by
/// the Azure SDK for testing purposes.
/// </summary>
internal sealed class FakeServiceBusProcessor : ServiceBusProcessor
{
    /// <summary>Whether <see cref="StartProcessingAsync"/> was called.</summary>
    public bool StartCalled { get; private set; }

    /// <summary>Whether <see cref="StopProcessingAsync"/> was called.</summary>
    public bool StopCalled { get; private set; }

    /// <inheritdoc/>
    public override Task StartProcessingAsync(CancellationToken cancellationToken = default)
    {
        StartCalled = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task StopProcessingAsync(CancellationToken cancellationToken = default)
    {
        StopCalled = true;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Fake <see cref="IServiceBusSenderWrapper"/> that records sent messages without
/// connecting to Azure.
/// </summary>
internal sealed class FakeServiceBusSenderWrapper(
    string queueName,
    FakeServiceBusAdapterClientFactory factory) : IServiceBusSenderWrapper
{
    /// <summary>The queue name this sender targets.</summary>
    public string QueueName { get; } = queueName;

    /// <summary>All messages sent via <see cref="SendMessageAsync"/>.</summary>
    public List<ServiceBusMessage> SentMessages { get; } = [];

    private readonly object _sync = new();

    /// <inheritdoc/>
    public Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        if (factory.FailNextSend)
        {
            factory.FailNextSend = false;
            throw new InvalidOperationException("Synthetic Service Bus send failure.");
        }

        lock (_sync)
            SentMessages.Add(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
