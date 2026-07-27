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
