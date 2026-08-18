using BotNexus.Cron;
using BotNexus.Extensions.Plugins.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BotNexus.Extensions.Plugins.Cron;

/// <summary>
/// Composition for the <c>plugin-update</c> cron action and its platform-wide provisioner (#2683).
/// </summary>
/// <remarks>
/// Registration lives here, in the plugin project, rather than in <c>AddBotNexusCron</c>: the
/// scheduler must not learn about plugins to schedule them. The action is contributed to the same
/// <c>ICronAction</c> enumerable every built-in action uses, so <c>CronScheduler</c> dispatches
/// <c>plugin-update</c> by exactly the mechanism it dispatches <c>command</c> - there is no second
/// dispatch path and no special case.
/// </remarks>
public static class PluginCronServiceCollectionExtensions
{
    /// <summary>
    /// Registers the plugin-update cron action, its update service, and the install-time
    /// provisioner. Requires <c>ICronStore</c> and <see cref="PluginLifecycleManager"/> to be
    /// registered by the host.
    /// </summary>
    /// <param name="services">Service collection to add to.</param>
    public static IServiceCollection AddPluginUpdateCron(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPluginUpdateService>(sp => sp.GetRequiredService<PluginLifecycleManager>());
        services.TryAddSingleton<PluginUpdateCronProvisioner>();
        services.TryAddSingleton<IPluginInstallObserver>(sp => sp.GetRequiredService<PluginUpdateCronProvisioner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICronAction, PluginUpdateCronAction>());

        return services;
    }
}
