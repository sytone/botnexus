using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// End-to-end coverage for issue #2390: the recent-log buffer must be fed by the REAL host
/// logging pipeline. These tests resolve the store from a built host container and log through
/// <see cref="ILogger{TCategoryName}"/> backed by the shared gateway Serilog configuration -
/// nothing writes to the store directly.
/// </summary>
public sealed class RecentLogStoreHostLoggingTests
{
    [Fact]
    public void HostLogger_WritesThroughSerilog_IntoRecentLogStore()
    {
        using var harness = new HostLoggingHarness();

        harness.CreateLogger<RecentLogStoreHostLoggingTests>()
            .LogWarning("host pipeline entry {Marker}", "2390");

        var entries = harness.Store.GetRecent(50);
        entries.ShouldNotBeEmpty();
        var entry = entries.First(candidate => candidate.Message.Contains("host pipeline entry"));
        entry.Level.ShouldBe("Warning");
        entry.Category.ShouldContain(nameof(RecentLogStoreHostLoggingTests));
        entry.Properties["Marker"].ShouldBe("2390");
    }

    [Fact]
    public void LogEndpoint_ReturnsEntriesProducedByHostPipeline()
    {
        using var harness = new HostLoggingHarness();

        var controller = new LogController(harness.CreateLogger<LogController>(), harness.Store);
        controller.Post(new ClientLogEntry("error", "endpoint-visible", "{}", "1", null));

        var result = controller.GetRecent(limit: 10);
        var entries = (result.Result as OkObjectResult)?.Value as IReadOnlyList<RecentLogEntry>;
        entries.ShouldNotBeNull();
        entries!.ShouldContain(entry => entry.Message.Contains("endpoint-visible"));
    }

    /// <summary>
    /// Issue #3282: the gateway's minimum log level must be changeable on a RUNNING logger. Both
    /// assertions use one already-built pipeline - nothing is rebuilt between them - so a pass proves
    /// the level is controlled by the runtime switch rather than baked in at <c>CreateLogger()</c>.
    /// Without this, enabling provider request logging would still require a gateway restart.
    /// Lives in this class so xUnit serialises it against the other tests that share the static switch.
    /// </summary>
    [Fact]
    public void LogLevelSwitch_ChangesEffectiveLevel_WithoutRebuildingTheLogger()
    {
        var original = GatewaySerilogConfiguration.LevelSwitch.MinimumLevel;
        try
        {
            using var harness = new HostLoggingHarness();
            var logger = harness.CreateLogger<RecentLogStoreHostLoggingTests>();

            // Raised to Debug, as `botnexus config set gateway.logLevel Debug` would.
            GatewaySerilogConfiguration.ApplyLogLevel("Debug").ShouldBeTrue();
            logger.LogDebug("switch entry {Marker}", "3282-on");
            harness.Store.GetRecent(50)
                .ShouldContain(entry => entry.Message.Contains("3282-on"));

            // Lowered again on the same logger: the Debug entry must now be suppressed.
            GatewaySerilogConfiguration.ApplyLogLevel("Warning").ShouldBeTrue();
            logger.LogDebug("switch entry {Marker}", "3282-off");
            harness.Store.GetRecent(50)
                .ShouldNotContain(entry => entry.Message.Contains("3282-off"));
        }
        finally
        {
            GatewaySerilogConfiguration.LevelSwitch.MinimumLevel = original;
        }
    }

    /// <summary>
    /// A typo in <c>gateway.logLevel</c> must not silently blind the operator's logs, so an
    /// unrecognised value is reported as not-applied and leaves the current level untouched.
    /// </summary>
    [Fact]
    public void ApplyLogLevel_RejectsUnknownValues_AndPreservesCurrentLevel()
    {
        var original = GatewaySerilogConfiguration.LevelSwitch.MinimumLevel;
        try
        {
            GatewaySerilogConfiguration.ApplyLogLevel("Debug").ShouldBeTrue();

            GatewaySerilogConfiguration.ApplyLogLevel("Verbos").ShouldBeFalse();
            GatewaySerilogConfiguration.ApplyLogLevel(null).ShouldBeFalse();
            GatewaySerilogConfiguration.ApplyLogLevel("  ").ShouldBeFalse();

            GatewaySerilogConfiguration.LevelSwitch.MinimumLevel
                .ShouldBe(Serilog.Events.LogEventLevel.Debug);
        }
        finally
        {
            GatewaySerilogConfiguration.LevelSwitch.MinimumLevel = original;
        }
    }

    /// <summary>
    /// <c>gateway.logLevel</c> uses Microsoft.Extensions.Logging names while Serilog uses its own;
    /// both spellings must map, or an operator setting "Trace" or "Critical" gets silence.
    /// </summary>
    [Theory]
    [InlineData("Trace", Serilog.Events.LogEventLevel.Verbose)]
    [InlineData("Verbose", Serilog.Events.LogEventLevel.Verbose)]
    [InlineData("debug", Serilog.Events.LogEventLevel.Debug)]
    [InlineData("Information", Serilog.Events.LogEventLevel.Information)]
    [InlineData("Warning", Serilog.Events.LogEventLevel.Warning)]
    [InlineData("Error", Serilog.Events.LogEventLevel.Error)]
    [InlineData("Critical", Serilog.Events.LogEventLevel.Fatal)]
    public void ApplyLogLevel_MapsConfiguredNames(string configured, Serilog.Events.LogEventLevel expected)
    {
        var original = GatewaySerilogConfiguration.LevelSwitch.MinimumLevel;
        try
        {
            GatewaySerilogConfiguration.ApplyLogLevel(configured).ShouldBeTrue();
            GatewaySerilogConfiguration.LevelSwitch.MinimumLevel.ShouldBe(expected);
        }
        finally
        {
            GatewaySerilogConfiguration.LevelSwitch.MinimumLevel = original;
        }
    }

    // Builds a real host container plus the shipped gateway Serilog configuration, mirroring how
    // Program.cs hands Serilog ownership of the ILoggerFactory.
    private sealed class HostLoggingHarness : IDisposable
    {
        private readonly string _logDirectory;
        private readonly IHost _host;
        private readonly Serilog.Core.Logger _serilogLogger;
        private readonly ILoggerFactory _loggerFactory;

        public HostLoggingHarness()
        {
            _logDirectory = Path.Combine(Path.GetTempPath(), "botnexus-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_logDirectory);

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddGatewayRecentLogStore();
            _host = builder.Build();

            _serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .ConfigureGatewayLogging(builder.Configuration, _host.Services, _logDirectory)
                .CreateLogger();

            _loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddSerilog(_serilogLogger);
            });
        }

        public IRecentLogStore Store => _host.Services.GetRequiredService<IRecentLogStore>();

        public ILogger<T> CreateLogger<T>() => _loggerFactory.CreateLogger<T>();

        public void Dispose()
        {
            _loggerFactory.Dispose();
            _serilogLogger.Dispose();
            _host.Dispose();
            if (Directory.Exists(_logDirectory))
                Directory.Delete(_logDirectory, recursive: true);
        }
    }
}
