using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Session;

/// <summary>Extension methods for registering session services.</summary>
public static class SessionServiceExtensions
{
    /// <summary>Registers the file-backed session manager.</summary>
    public static IServiceCollection AddBotNexusSession(this IServiceCollection services)
    {
        services.AddSingleton<ISessionManager>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<BotNexusConfig>>().Value;
            var storePath = Environment.ExpandEnvironmentVariables(
                config.Agents.Workspace.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            var sessionsPath = Path.Combine(storePath, "sessions");
            var logger = sp.GetRequiredService<ILogger<SessionManager>>();
            return new SessionManager(sessionsPath, logger);
        });
        return services;
    }
}
