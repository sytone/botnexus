using BotNexus.Agent.Tools;
using BotNexus.Core.Abstractions;
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
    /// Registers tools from all <see cref="ITool"/> services into the provided <see cref="ToolRegistry"/>.
    /// Call this after building the service provider to wire extension tools into an agent registry.
    /// </summary>
    public static ToolRegistry RegisterFromServices(this ToolRegistry registry, IServiceProvider services)
    {
        var tools = services.GetServices<ITool>();
        registry.RegisterRange(tools);
        return registry;
    }
}
