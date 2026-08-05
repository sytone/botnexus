using BotNexus.Cron.Actions;
using BotNexus.Cron.Prompts;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
            return new SqliteCronStore(
                Path.Combine(rootPath, "cron.sqlite"),
                new FileSystem(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<SqliteCronStore>>());
        });
        services.TryAddSingleton<HeartbeatCronProvisioner>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<HeartbeatCronProvisioner>());
        // Also expose as IHeartbeatProvisioner so AgentsController can call ProvisionAsync at runtime.
        services.TryAddSingleton<IHeartbeatProvisioner>(sp => sp.GetRequiredService<HeartbeatCronProvisioner>());
        services.TryAddSingleton<SkillReviewCronProvisioner>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SkillReviewCronProvisioner>());
        // Also expose as ISkillReviewProvisioner so AgentsController can call ProvisionAsync at runtime.
        services.TryAddSingleton<ISkillReviewProvisioner>(sp => sp.GetRequiredService<SkillReviewCronProvisioner>());
        services.TryAddSingleton<CronScheduler>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CronScheduler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICronAction, AgentPromptAction>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICronAction, HeartbeatAction>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICronAction, WebhookAction>());
        // #2462: firing-time authorization seam for command cron jobs. Registered here so
        // CommandCronAction resolves a real authorizer instead of a local fallback instance.
        services.TryAddSingleton<ICommandCronAuthorizer, ToolPolicyCommandCronAuthorizer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICronAction, CommandCronAction>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICronAction, MemoryDreamingCronAction>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICronAction, SkillReviewCronAction>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICronAction, AgentConverseCronAction>());
        services.TryAddSingleton<IPromptTemplateResolver, CronOptionsPromptTemplateResolver>();
        services.AddOptions<CronRunRetentionOptions>();
        services.AddSingleton<IHostedService, CronRunRetentionHostedService>();
        services.AddSingleton<IHostedService, MissedRunDetectionService>();
        return services;
    }

    /// <summary>
    /// Resolves the directory that holds <c>cron.sqlite</c>.
    /// </summary>
    /// <remarks>
    /// This binds to <see cref="BotNexusHome"/> DIRECTLY, by type, not through
    /// <c>Type.GetType("..., BotNexus.Gateway")</c>. The reflection form was a silent
    /// correctness hazard and it fired in production (#2819): an assembly-qualified name is a
    /// STRING, so when #2765/#2777 extracted BotNexusHome into BotNexus.Gateway.Configuration the
    /// lookup began returning null at runtime with nothing failing at compile time. Every caller
    /// then fell through to the user-profile default below and opened the LIVE
    /// <c>~/.botnexus/cron.sqlite</c> -- ignoring an explicitly supplied home. Test gateways
    /// started with an isolated <c>--target</c> home claimed the developer's real scheduled jobs
    /// and failed them with "Agent 'farnsworth' is not registered".
    ///
    /// A direct project reference makes the same mistake impossible: moving the type again breaks
    /// the BUILD instead of silently redirecting production state. There is no circular
    /// dependency -- BotNexus.Gateway.Configuration does not reference BotNexus.Cron.
    /// </remarks>
    private static string ResolveRootPath(IServiceProvider services)
    {
        var home = services.GetService<BotNexusHome>();

        // Prefer the writable data directory (BOTNEXUS_DATA_DIR) so cron.sqlite works even when
        // the config directory (RootPath) is mounted read-only; fall back to RootPath locally.
        if (!string.IsNullOrWhiteSpace(home?.DataPath))
            return home.DataPath;

        if (!string.IsNullOrWhiteSpace(home?.RootPath))
            return home.RootPath;

        // No home was registered at all. The user-profile default is a genuine last resort for
        // hosts that compose cron without the gateway's configuration stack -- but it silently
        // targets SHARED, LIVE state, so it must never be taken quietly. #2819 went undiagnosed
        // for days precisely because this branch produced a working-looking store.
        var fallback = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".botnexus"));

        services.GetService<ILogger<SqliteCronStore>>()?.LogWarning(
            "No {HomeType} was registered, so the cron store fell back to the shared user-profile path {FallbackPath}. " +
            "Any isolated home supplied by the host is being IGNORED and this process will read and claim jobs from the " +
            "live store (#2819).",
            nameof(BotNexusHome),
            fallback);

        return fallback;
    }
}
