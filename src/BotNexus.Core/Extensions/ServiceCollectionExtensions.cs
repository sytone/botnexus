using BotNexus.Core.Abstractions;
using BotNexus.Core.Bus;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Core.Extensions;

/// <summary>Extension methods for registering BotNexus core services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers all BotNexus core services into the DI container.</summary>
    public static IServiceCollection AddBotNexusCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BotNexusConfig>(configuration.GetSection(BotNexusConfig.SectionName));
        services.AddSingleton<IMessageBus>(sp => new MessageBus(capacity: 1000));
        services.AddSingleton<IActivityStream, ActivityStream>();
        return services;
    }
}
