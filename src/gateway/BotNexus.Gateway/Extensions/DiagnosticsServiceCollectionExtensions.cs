using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Extensions;

/// <summary>
/// Registers additional diagnostics services for liveness hardening:
/// threadpool queue monitoring and lock timeout logging.
/// </summary>
public static class DiagnosticsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the threadpool watchdog and lock timeout logger to the service collection.
    /// </summary>
    public static IServiceCollection AddDiagnosticsHardening(this IServiceCollection services)
    {
        services.AddSingleton<IActiveLoopTracker, ActiveLoopTracker>();
        services.AddSingleton<IThreadPoolMetrics, SystemThreadPoolMetrics>();
        services.Configure<ThreadPoolWatchdogOptions>(_ => { });
        services.AddHostedService<ThreadPoolWatchdogService>();
        services.AddSingleton<LockTimeoutLogger>();
        return services;
    }
}
