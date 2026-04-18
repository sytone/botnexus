using BotNexus.Cron.Actions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.IO.Abstractions;

namespace BotNexus.Cron.Extensions;

public static class CronServiceCollectionExtensions
{
    public static IServiceCollection AddBotNexusCron(this IServiceCollection services)
    {
        services.AddOptions<CronOptions>();
        services.TryAddSingleton<ICronStore>(sp =>
        {
            var rootPath = ResolveRootPath(sp);
            return new SqliteCronStore(Path.Combine(rootPath, "cron.sqlite"), new FileSystem());
        });
        services.TryAddSingleton<HeartbeatCronProvisioner>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<HeartbeatCronProvisioner>());
        services.TryAddSingleton<CronScheduler>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CronScheduler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICronAction, AgentPromptAction>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICronAction, WebhookAction>());
        return services;
    }

    private static string ResolveRootPath(IServiceProvider services)
    {
        var homeType = Type.GetType("BotNexus.Gateway.Configuration.BotNexusHome, BotNexus.Gateway");
        var home = homeType is null ? null : services.GetService(homeType);
        var rootPath = homeType?.GetProperty("RootPath")?.GetValue(home) as string;
        if (!string.IsNullOrWhiteSpace(rootPath))
            return rootPath;

        return Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".botnexus"));
    }
}
