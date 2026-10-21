using System.IO.Abstractions;
using BotNexus.Extensions.Plugins.Cron;
using BotNexus.Extensions.Plugins.Security;
using BotNexus.Extensions.Mcp;
using BotNexus.Extensions.Mcp.Plugins;
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
        // The API extension is a composition root, not the core on-disk model. Keeping the
        // adapter here avoids a Plugins -> Mcp dependency (or a cycle through Skills).
        services.TryAddSingleton<PluginTrustGate>(sp => new PluginTrustGate(
            ContentTrustMode.Disabled, sp.GetRequiredService<IFileSystem>(), sp.GetRequiredService<ILogger<PluginTrustGate>>()));
        services.TryAddSingleton<IPluginTrustEvaluator, PluginMcpTrustEvaluator>();
        services.TryAddSingleton<McpServerManager>(sp => new McpServerManager(sp.GetRequiredService<ILogger<McpServerManager>>()));
        services.TryAddSingleton<IMcpServerHost, McpServerManagerHost>();
        services.TryAddSingleton<PluginMcpServerRegistrar>(sp => new PluginMcpServerRegistrar(
            sp.GetRequiredService<IMcpServerHost>(), sp.GetRequiredService<IPluginTrustEvaluator>(),
            sp.GetRequiredService<ILogger<PluginMcpServerRegistrar>>()));
        services.TryAddSingleton<IPluginLifecycleParticipant, PluginMcpLifecycleParticipant>();
        services.TryAddSingleton<PluginLifecycleManager>(serviceProvider => new PluginLifecycleManager(
            serviceProvider.GetRequiredService<PluginStateStore>(),
            serviceProvider.GetRequiredService<IPluginSourceFetcher>(),
            logger: serviceProvider.GetRequiredService<ILogger<PluginLifecycleManager>>(),
            installObserver: serviceProvider.GetService<IPluginInstallObserver>(),
            participant: serviceProvider.GetRequiredService<IPluginLifecycleParticipant>()));
        services.TryAddSingleton<IPluginUpdateService>(serviceProvider =>
            serviceProvider.GetRequiredService<PluginLifecycleManager>());
    }
}
