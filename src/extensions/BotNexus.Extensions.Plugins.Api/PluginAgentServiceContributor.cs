using System.IO.Abstractions;
using BotNexus.Extensions.Plugins.Agents;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Plugins.Api;

/// <summary>
/// Adds plugin-shipped agent definitions to the gateway's existing configuration-source pipeline.
/// </summary>
/// <remarks>
/// The plugins API assembly is already a production-loaded extension. Registering through its
/// <see cref="IServiceContributor"/> keeps the gateway independent of extension implementation
/// assemblies while ensuring <see cref="AgentConfigurationHostedService"/> receives this source
/// alongside the platform-config source. The source is constructed from host-owned state: the
/// writable BotNexus data root and the current gateway file-access ceiling.
/// </remarks>
public sealed class PluginAgentServiceContributor : IServiceContributor
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAgentConfigurationSource>(serviceProvider =>
        {
            var home = serviceProvider.GetRequiredService<BotNexusHome>();
            var options = serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
            var fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
            var logger = serviceProvider.GetRequiredService<ILogger<PluginAgentConfigurationSource>>();

            return new PluginAgentConfigurationSource(
                fileSystem.Path.Combine(home.DataPath, PluginSkillRootResolver.PluginRootDirectoryName),
                () => ToPolicy(options.CurrentValue.Gateway?.FileAccess),
                logger,
                fileSystem);
        });
    }

    private static FileAccessPolicy? ToPolicy(FileAccessPolicyConfig? config) => config is null
        ? null
        : new FileAccessPolicy
        {
            AllowedReadPaths = config.AllowedReadPaths?.ToArray() ?? [],
            AllowedWritePaths = config.AllowedWritePaths?.ToArray() ?? [],
            DeniedPaths = config.DeniedPaths?.ToArray() ?? []
        };
}
