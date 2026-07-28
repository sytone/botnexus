using BotNexus.Extensions.Channels.ServiceBus.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Extensions.Channels.ServiceBus.Tests;

/// <summary>
/// #2386 - the Service Bus processor error callback logged at ERR and did nothing else, so the
/// SDK kept re-attempting a receive that could never succeed. A revoked AAD grant (AADSTS50173)
/// produced 4,768 ERR lines at ~13/min for six hours while inbound messages were silently not
/// received. These tests assert the receive loop is now parked on a terminal fault.
/// </summary>
public sealed class ServiceBusReceiveLoopBoundingTests
{
    private static ServiceBusChannelAdapter CreateStartedAdapter(FakeServiceBusAdapterClientFactory factory)
    {
        var adapter = new ServiceBusChannelAdapter(
            NullLogger<ServiceBusChannelAdapter>.Instance,
            new OptionsWrapper<ServiceBusChannelOptions>(new ServiceBusChannelOptions
            {
                ConnectionString = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=FAKE=",
                InboundQueueName = "test-inbound",
                DefaultReplyQueueName = "test-outbound",
            }),
            factory);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        adapter.StartAsync(dispatcher.Object).GetAwaiter().GetResult();
        return adapter;
    }

    [Fact]
    public async Task RevokedCredential_TripsTheBreakerAndStopsTheProcessor()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateStartedAdapter(factory);

        factory.Processor.StopCalled.ShouldBeFalse();

        // The observed incident shape: a credential fault, which no retry can clear.
        var revoked = new UnauthorizedAccessException(
            "AADSTS50173: The provided grant has expired due to it being revoked, a fresh auth token is needed.");

        await adapter.HandleProcessorErrorAsync(revoked, "Receive", "test-inbound");

        adapter.ReceiveCircuitIsOpen.ShouldBeTrue();
        factory.Processor.StopCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task UnclassifiableFailure_FailsClosedAndStopsTheProcessor()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateStartedAdapter(factory);

        await adapter.HandleProcessorErrorAsync(new NotSupportedException("nobody classified this"), "Receive", "test-inbound");

        adapter.ReceiveCircuitIsOpen.ShouldBeTrue();
        factory.Processor.StopCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task TransientFailure_LeavesTheProcessorRunning()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateStartedAdapter(factory);

        // A momentary broker blip must NOT take the transport down until the next restart.
        for (var i = 0; i < 5; i++)
            await adapter.HandleProcessorErrorAsync(new TimeoutException("broker blip"), "Receive", "test-inbound");

        adapter.ReceiveCircuitIsOpen.ShouldBeFalse();
        factory.Processor.StopCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task HealthyAdapter_IsUnaffected()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateStartedAdapter(factory);

        factory.Processor.StartCalled.ShouldBeTrue();
        adapter.ReceiveCircuitIsOpen.ShouldBeFalse();
        factory.Processor.StopCalled.ShouldBeFalse();

        await adapter.StopAsync(CancellationToken.None);
    }
}
