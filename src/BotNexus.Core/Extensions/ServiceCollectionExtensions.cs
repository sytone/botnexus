using BotNexus.Core.Abstractions;
using BotNexus.Core.Bus;
using BotNexus.Core.Configuration;
using BotNexus.Core.Observability;
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
        services.AddSingleton<SystemMessageStore>();
        services.AddSingleton<IActivityStream>(sp => 
        {
            var store = sp.GetRequiredService<SystemMessageStore>();
            return new ActivityStream(store);
        });
        services.AddSingleton<IBotNexusMetrics, BotNexusMetrics>();
        services.AddSingleton<ISystemActionRegistry, SystemActionRegistry>();
        return services;
    }
}
