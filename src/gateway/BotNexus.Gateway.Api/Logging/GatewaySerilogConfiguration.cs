using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Configuration;
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

        // #3276: the redactor is resolved from the host so operator-supplied patterns (#2727)
        // apply to logs too - but LAZILY, on first use. Resolving it here would force the
        // ISecretRedactor factory (and with it the whole IOptions<PlatformConfig> graph) to be
        // built during host startup, before the options pipeline has settled, which is a startup-
        // ordering hazard for a component that only needs to exist by the time an event is written.
        // When no registration is present - the bootstrap path, or a test host that wires logging
        // without the full gateway container - we fall back to the built-in redactor rather than to
        // no redaction: a missing registration is a wiring gap, not a licence to write credentials.
        var redactor = new LazySecretRedactor(services);

        var enriched = configuration
            .ReadFrom.Configuration(hostConfiguration)
            .ReadFrom.Services(services)
            // Must come AFTER ReadFrom.Configuration so the switch wins over any static
            // MinimumLevel in config: the whole point is that this one is changeable at runtime.
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId();

        // Every sink is wrapped in one redacting decorator rather than each being wrapped
        // individually, so a sink added later inherits redaction by construction instead of by the
        // author remembering to opt in - which is precisely the failure this issue reports.
        var redactingSink = LoggerSinkConfiguration.Wrap(
            inner => new SecretRedactingSink(inner, redactor),
            wrapped =>
            {
                wrapped.Sink(new RecentLogStoreSink(services.GetRequiredService<IRecentLogStore>()));
                wrapped.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
                    restrictedToMinimumLevel: LogEventLevel.Warning);
                wrapped.File(
                    Path.Combine(logDirectory, "botnexus-.log"),
                    rollingInterval: RollingInterval.Hour,
                    retainedFileCountLimit: 168);
            });

        return enriched.WriteTo.Sink(redactingSink);
    }

    /// <summary>
    /// Applies the bootstrap logger's sinks with the same redaction guarantee (#3276). The
    /// bootstrap logger runs before the DI container exists, so it uses the built-in
    /// <see cref="SecretRedactor"/> pattern set; operator-supplied patterns are not yet loadable at
    /// that point. Startup logging is short-lived but still writes to disk, and it is exactly where
    /// channel credentials are first read.
    /// </summary>
    /// <param name="configuration">The bootstrap Serilog configuration.</param>
    /// <param name="bootstrapLogPath">Path template for the rolling bootstrap log file.</param>
    /// <returns>The same configuration instance, for chaining.</returns>
    public static LoggerConfiguration ConfigureBootstrapLogging(
        this LoggerConfiguration configuration,
        string bootstrapLogPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapLogPath);

        var redactor = new SecretRedactor();

        var redactingSink = LoggerSinkConfiguration.Wrap(
            inner => new SecretRedactingSink(inner, redactor),
            wrapped =>
            {
                wrapped.Console();
                wrapped.File(bootstrapLogPath, rollingInterval: RollingInterval.Day);
            });

        return configuration.WriteTo.Sink(redactingSink);
    }

    /// <summary>
    /// Defers <see cref="ISecretRedactor"/> resolution to the first log event so that configuring
    /// logging never forces the gateway's options graph to be built during host startup (#3276).
    /// Falls back to the built-in <see cref="SecretRedactor"/> when the container has no
    /// registration, so redaction is never silently switched off.
    /// </summary>
    private sealed class LazySecretRedactor(IServiceProvider services) : ISecretRedactor
    {
        // The built-in pattern set is always available and needs no container. It is the fallback
        // for every path where the configured redactor cannot be obtained safely, so redaction is
        // never skipped - the worst case is that operator-supplied patterns are not applied.
        private static readonly SecretRedactor BuiltIn = new();

        // Guards against re-entrancy: resolving ISecretRedactor builds the options graph, and
        // configuration loading logs. Without this flag that log call would re-enter resolution
        // from the same thread and deadlock host startup - which is exactly how the first attempt
        // at this fix hung the extension-boot gateway for its full three-minute readiness budget.
        [ThreadStatic]
        private static bool _resolving;

        private ISecretRedactor? _resolved;

        public string Redact(string input) => Current.Redact(input);

        public string RedactForExternalDelivery(string input) => Current.RedactForExternalDelivery(input);

        private ISecretRedactor Current
        {
            get
            {
                if (_resolved is not null)
                    return _resolved;

                if (_resolving)
                    return BuiltIn;

                _resolving = true;
                try
                {
                    // Cached on success only. A null/failed resolve leaves _resolved unset so a
                    // later event - once the container is fully built - can still pick up the
                    // operator-configured redactor.
                    _resolved = services.GetService<ISecretRedactor>();
                }
                catch (Exception)
                {
                    // Never throw from the logging path: a redactor that cannot be resolved must
                    // degrade to the built-in patterns, not take down logging.
                }
                finally
                {
                    _resolving = false;
                }

                return _resolved ?? BuiltIn;
            }
        }
    }
}
