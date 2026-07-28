using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace BotNexus.Gateway.Api.Logging;

/// <summary>
/// Single definition of the gateway's Serilog pipeline and of the recent-log store registration.
/// Program.cs and the host-level logging tests both go through here so the wiring under test is
/// the wiring that ships - in particular the <see cref="RecentLogStoreSink"/> that feeds
/// <c>GET /api/logs/recent</c> (issue #2390).
/// </summary>
public static class GatewaySerilogConfiguration
{
    /// <summary>
    /// Registers the in-process diagnostics buffer read by <c>GET /api/logs/recent</c>. Kept
    /// separate from the full API registration so logging can be wired before (and independently
    /// of) controllers, triggers and hosted services.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddGatewayRecentLogStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRecentLogStore, InMemoryRecentLogStore>();
        return services;
    }

    /// <summary>
    /// Applies the gateway's standard enrichers and sinks to <paramref name="configuration"/>.
    /// The recent-log sink is attached here rather than as a DI <c>ILoggerProvider</c> because
    /// <c>UseSerilog</c> replaces the host <c>ILoggerFactory</c> and would silently drop it.
    /// </summary>
    /// <param name="configuration">The Serilog configuration supplied by the host builder.</param>
    /// <param name="hostConfiguration">Host configuration used for Serilog's own settings section.</param>
    /// <param name="services">Resolved host services; supplies the <see cref="IRecentLogStore"/>.</param>
    /// <param name="logDirectory">Directory that receives the rolling log files.</param>
    /// <returns>The same configuration instance, for chaining.</returns>
    public static LoggerConfiguration ConfigureGatewayLogging(
        this LoggerConfiguration configuration,
        IConfiguration hostConfiguration,
        IServiceProvider services,
        string logDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostConfiguration);
        ArgumentNullException.ThrowIfNull(services);

        return configuration
            .ReadFrom.Configuration(hostConfiguration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .WriteTo.Sink(new RecentLogStoreSink(services.GetRequiredService<IRecentLogStore>()))
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(logDirectory, "botnexus-.log"),
                rollingInterval: RollingInterval.Hour,
                retainedFileCountLimit: 168);
    }
}
