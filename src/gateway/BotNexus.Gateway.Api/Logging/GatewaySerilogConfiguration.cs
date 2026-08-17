using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

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
    /// The process-wide Serilog level switch (issue #3282). Serilog fixes its minimum level when the
    /// logger is built, so a level read straight from configuration can only ever change on restart.
    /// Routing the level through a switch instead lets <c>gateway.logLevel</c> - and therefore
    /// provider request logging - be raised to <c>Debug</c> and lowered again on a running gateway.
    /// Static because <c>UseSerilog</c> builds the logger before the service provider exists, so the
    /// switch has to outlive DI construction and be reachable from both sides of it.
    /// </summary>
    public static LoggingLevelSwitch LevelSwitch { get; } = new(LogEventLevel.Information);

    /// <summary>
    /// Applies <paramref name="logLevel"/> to <see cref="LevelSwitch"/>, taking effect immediately
    /// for every subsequent log call. Accepts the <c>Microsoft.Extensions.Logging</c> names used by
    /// <c>gateway.logLevel</c> (<c>Trace</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>,
    /// <c>Error</c>, <c>Critical</c>) as well as Serilog's own spellings. An unrecognised or empty
    /// value leaves the current level untouched rather than silently resetting it: a typo in config
    /// must not blind the operator's logs.
    /// </summary>
    /// <param name="logLevel">Configured level name; may be <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the level was recognised and applied.</returns>
    public static bool ApplyLogLevel(string? logLevel)
    {
        if (string.IsNullOrWhiteSpace(logLevel))
            return false;

        var mapped = logLevel.Trim().ToLowerInvariant() switch
        {
            "trace" or "verbose" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" or "info" => LogEventLevel.Information,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "critical" or "fatal" => LogEventLevel.Fatal,
            _ => (LogEventLevel?)null,
        };

        if (mapped is null)
            return false;

        LevelSwitch.MinimumLevel = mapped.Value;
        return true;
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
            // Must come AFTER ReadFrom.Configuration so the switch wins over any static
            // MinimumLevel in config: the whole point is that this one is changeable at runtime.
            .MinimumLevel.ControlledBy(LevelSwitch)
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
