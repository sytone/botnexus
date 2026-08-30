using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests.Helpers;

/// <summary>
/// Registers SignalR channel extension services for integration tests.
/// In production, these are loaded dynamically by the extension loader.
/// </summary>
public static class SignalRTestServiceExtensions
{
    public static IServiceCollection AddSignalRChannelForTests(this IServiceCollection services)
    {
        services.AddSingleton<SignalRChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(sp => sp.GetRequiredService<SignalRChannelAdapter>());
        services.AddSingleton<IEndpointContributor, SignalREndpointContributor>();
        services.AddSingleton<IUserIdProvider, ClaimsUserIdProvider>();
        services.AddSignalRAuthPolicy();

        // #3679: mirror production's global connection-abort filter registration.
        services.AddSingleton<ConnectionAbortHubFilter>();
        services.AddOptions<HubOptions<GatewayHub>>()
            .Configure<ConnectionAbortHubFilter>((options, filter) => options.AddFilter(filter));

        // GatewayHub now resolves the gateway application collaborators through a single facade
        // (registered in production by SignalRServiceContributor). Register the same facade here
        // so the test host can construct the hub. IConversationResetService is optional, matching
        // production, so hosts that do not register one still start.
        services.AddSingleton<IGatewayHubApplicationService>(sp =>
            new GatewayHubApplicationService(
                sp.GetRequiredService<IInboundMessageOrchestrator>(),
                sp.GetRequiredService<ISessionWarmupService>(),
                sp.GetRequiredService<IConversationDispatcher>(),
                sp.GetRequiredService<ISessionCompactionCoordinator>(),
                sp.GetService<IConversationResetService>(),
                sp.GetService<ILogger<GatewayHubApplicationService>>()));
        return services;
    }
}
