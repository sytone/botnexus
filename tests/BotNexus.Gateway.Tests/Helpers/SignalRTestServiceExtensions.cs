using BotNexus.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.Extensions.DependencyInjection;

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
        return services;
    }
}
