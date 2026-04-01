using BotNexus.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Tools.GitHub;

/// <summary>
/// Extension methods for registering BotNexus GitHub tools with the DI container.
/// </summary>
public static class GitHubToolsServiceExtensions
{
    /// <summary>
    /// Registers the <see cref="GitHubTool"/> as an <see cref="ITool"/> in the DI container
    /// so it is automatically picked up by the agent's <see cref="ToolRegistry"/>.
    ///
    /// <para>Add this to your gateway DI setup:</para>
    /// <code>
    /// services.AddGitHubTools(config =>
    /// {
    ///     config.Token = "ghp_...";
    ///     config.DefaultOwner = "my-org";
    /// });
    /// </code>
    /// </summary>
    public static IServiceCollection AddGitHubTools(
        this IServiceCollection services,
        Action<GitHubToolsConfig>? configure = null)
    {
        if (configure is not null)
            services.Configure<GitHubToolsConfig>(configure);
        else
            services.AddOptions<GitHubToolsConfig>();

        // Register the tool itself as ITool so it can be discovered
        services.AddSingleton<ITool>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<GitHubToolsConfig>>().Value;
            var logger = sp.GetService<ILogger<GitHubTool>>();
            return new GitHubTool(config, logger: logger);
        });

        return services;
    }

    /// <summary>
    /// Registers the GitHub tool by binding its options from an extension configuration section.
    /// </summary>
    public static IServiceCollection AddGitHubTools(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GitHubToolsConfig>(configuration);
        services.AddSingleton<ITool>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<GitHubToolsConfig>>().Value;
            var logger = sp.GetService<ILogger<GitHubTool>>();
            return new GitHubTool(config, logger: logger);
        });

        return services;
    }
}

/// <summary>
/// Extension registrar used by the dynamic extension loader.
/// </summary>
public sealed class GitHubExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
        => services.AddGitHubTools(configuration);
}
