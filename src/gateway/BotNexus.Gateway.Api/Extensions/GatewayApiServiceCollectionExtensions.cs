using BotNexus.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Logging;
using BotNexus.Gateway.Api.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Extensions;

/// <summary>
/// DI registration and endpoint mapping extensions for the Gateway API layer.
/// </summary>
public static class GatewayApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Gateway API services (controllers, channel extensions, triggers).
    /// Call after <c>AddBotNexusGateway()</c>.
    /// </summary>
    public static IServiceCollection AddBotNexusGatewayApi(this IServiceCollection services)
    {
        services.AddSingleton<IRecentLogStore, InMemoryRecentLogStore>();
        services.AddSingleton<ILoggerProvider>(serviceProvider =>
            new RecentLogEntryLoggerProvider(serviceProvider.GetRequiredService<IRecentLogStore>()));
        services.AddSingleton<CronTrigger>();
        services.AddSingleton<SoulTrigger>();
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<CronTrigger>());
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<SoulTrigger>());

        // Built-in SignalR channel extension — registered as a project reference for now.
        // These will move to dynamic extension loading when the channel extension is deployed
        // to the extensions directory with its own manifest (Phase 2).
        services.AddSingleton<SignalRChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(provider => provider.GetRequiredService<SignalRChannelAdapter>());
        services.AddHostedService<SubAgentSignalRBridge>();
        services.AddSingleton<IEndpointContributor, SignalREndpointContributor>();

        services.AddControllers()
            .AddApplicationPart(typeof(GatewayApiServiceCollectionExtensions).Assembly);

        return services;
    }
}
