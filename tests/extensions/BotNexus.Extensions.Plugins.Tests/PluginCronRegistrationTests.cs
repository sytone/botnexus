using BotNexus.Cron;
using BotNexus.Extensions.Plugins.Cron;
using BotNexus.Extensions.Plugins.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the registration half of AC1 (#2683): <c>plugin-update</c> must reach the scheduler by the
/// same <c>ICronAction</c> enumerable every built-in action uses.
/// </summary>
/// <remarks>
/// This is what makes AC1 non-vacuous. A correct action class that no container ever contributes to
/// the enumerable is dispatched by nothing - <c>CronScheduler</c> resolves actions by scanning that
/// collection for a matching <c>ActionType</c>, so an unregistered action makes every run of the
/// job fail with "unknown action type" while every unit test of the action itself stays green.
/// </remarks>
public sealed class PluginCronRegistrationTests
{
    private static ServiceProvider Compose()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICronStore>(new FakeCronStore());
        services.AddSingleton(new PluginStateStore(
            Path.Combine(Path.GetTempPath(), "botnexus-plugin-registration", Guid.NewGuid().ToString("N"))));
        services.AddSingleton<IPluginSourceFetcher, GitPluginSourceFetcher>();
        services.AddSingleton<IGitCommandRunner>(new ProcessGitCommandRunner());
        services.AddSingleton<PluginLifecycleManager>();
        services.AddPluginUpdateCron();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheActionIsContributedToTheCronActionEnumerable()
    {
        using var provider = Compose();

        var actions = provider.GetServices<ICronAction>().ToList();

        // Resolved by ACTION TYPE, exactly the way CronScheduler finds it - not by concrete type,
        // which would pass even if the registered ActionType string were wrong.
        var match = actions.SingleOrDefault(a => a.ActionType == PluginUpdateCronAction.TypeName);
        Assert.NotNull(match);
        Assert.IsType<PluginUpdateCronAction>(match);
    }

    [Fact]
    public void TheUpdateServiceAndInstallObserverAreResolvable()
    {
        using var provider = Compose();

        // The action's dependency resolves, so a scheduled run cannot hit the
        // "no IPluginUpdateService is registered" fail-closed branch in a composed gateway.
        Assert.NotNull(provider.GetService<IPluginUpdateService>());

        var observer = provider.GetService<IPluginInstallObserver>();
        Assert.IsType<PluginUpdateCronProvisioner>(observer);
    }
}
