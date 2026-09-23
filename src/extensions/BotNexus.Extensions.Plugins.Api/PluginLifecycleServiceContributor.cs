using System.IO.Abstractions;
using BotNexus.Extensions.Plugins.Cron;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Plugins.Api;

/// <summary>
/// Composes the single production plugin-lifecycle graph used by HTTP and scheduled updates.
/// </summary>
public sealed class PluginLifecycleServiceContributor : IServiceContributor
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<PluginStateStore>(serviceProvider =>
        {
            var home = serviceProvider.GetRequiredService<BotNexusHome>();
            var fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
            return new PluginStateStore(
                fileSystem.Path.Combine(home.DataPath, PluginSkillRootResolver.PluginRootDirectoryName),
                fileSystem);
        });
        services.TryAddSingleton<ProcessGitCommandRunner>();
        services.TryAddSingleton<IGitCommandRunner>(serviceProvider =>
            serviceProvider.GetRequiredService<ProcessGitCommandRunner>());
        services.TryAddSingleton<GitPluginSourceFetcher>();
        services.TryAddSingleton<IPluginSourceFetcher>(serviceProvider =>
            serviceProvider.GetRequiredService<GitPluginSourceFetcher>());
        services.TryAddSingleton<PluginLifecycleManager>(serviceProvider => new PluginLifecycleManager(
            serviceProvider.GetRequiredService<PluginStateStore>(),
            serviceProvider.GetRequiredService<IPluginSourceFetcher>(),
            logger: serviceProvider.GetRequiredService<ILogger<PluginLifecycleManager>>(),
            installObserver: serviceProvider.GetService<IPluginInstallObserver>()));
        services.TryAddSingleton<IPluginUpdateService>(serviceProvider =>
            serviceProvider.GetRequiredService<PluginLifecycleManager>());
    }
}
