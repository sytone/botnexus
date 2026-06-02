using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BotNexus.Gateway.Tests.Integration;

/// <summary>
/// Test-only DI helpers for installing a recording dispatcher that intercepts
/// every inbound message flowing through the SignalR hub. The hub depends on
/// <see cref="IInboundMessageOrchestrator"/> (production wiring) and other
/// components still read <see cref="IChannelDispatcher"/>; both must be
/// replaced together so tests don't accidentally split the message flow
/// between the recorded fake and the production orchestrator.
/// </summary>
internal static class RecordingDispatcherRegistration
{
    /// <summary>
    /// Registers <paramref name="dispatcher"/> as the active
    /// <see cref="IChannelDispatcher"/> AND <see cref="IInboundMessageOrchestrator"/>,
    /// removing any previous registration of either interface. The dispatcher must
    /// implement both interfaces so the hub records every inbound message and the
    /// rest of the gateway sees a coherent dispatcher view.
    /// </summary>
    public static void UseRecordingDispatcher<T>(this IServiceCollection services, T dispatcher)
        where T : class, IChannelDispatcher, IInboundMessageOrchestrator
    {
        services.RemoveAll<IChannelDispatcher>();
        services.RemoveAll<IInboundMessageOrchestrator>();
        services.AddSingleton<IChannelDispatcher>(dispatcher);
        services.AddSingleton<IInboundMessageOrchestrator>(dispatcher);
    }
}
