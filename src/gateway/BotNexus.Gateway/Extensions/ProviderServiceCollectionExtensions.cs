using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Extensions;

/// <summary>
/// DI registration for provider health check services.
/// </summary>
public static class ProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers provider health check services.
    /// </summary>
    public static IServiceCollection AddProviderHealthCheck(this IServiceCollection services)
    {
        services.AddSingleton<IProviderHealthCheck, DefaultProviderHealthCheck>();
        return services;
    }
}
