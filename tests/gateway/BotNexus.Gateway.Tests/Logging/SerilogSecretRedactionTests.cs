using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Logging;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Parsing;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Coverage for issue #3276: the gateway's Serilog sinks wrote Telegram bot tokens to disk in
/// cleartext because <see cref="ISecretRedactor"/> was never applied on the sink path.
/// <para>
/// These tests drive the SHIPPED <see cref="GatewaySerilogConfiguration.ConfigureGatewayLogging"/>
/// wiring and then read the ROLLING FILE the gateway actually writes, rather than asserting against
/// a test-only sink. A capture sink attached by the test would sit outside the redacting wrapper
/// and would prove nothing about what lands on disk.
/// </para>
/// </summary>
public sealed class SerilogSecretRedactionTests
{
    // A real-shaped Telegram bot token: <digits>:<35 chars of base64url>. Not a live credential -
    // the digits and secret are synthetic - but it matches TelegramBotTokenRegex() exactly, which is
    // the whole point: a placeholder like "TOKEN" would make every assertion below vacuous.
    private const string TelegramToken = "8976578899:AAHXk1QwErTyUiOpAsDfGhJkLzXcVbNmQTP";
    private const string TelegramSecret = "AAHXk1QwErTyUiOpAsDfGhJkLzXcVbNmQTP";
    private const string Redacted = "[REDACTED]";

    [Fact]
    public void FileSink_DoesNotWriteTelegramTokenFromMessageProperty()
    {
        using var harness = new HarnessBuilder().Build();

        // This is the exact shape HttpClient's INF request logging emits, with the credential in a
        // structured property rather than in literal template text.
        harness.CreateLogger<SerilogSecretRedactionTests>()
            .LogInformation(
                "Start processing HTTP request {Method} {Uri}",
                "POST",
                $"https://api.telegram.org/bot{TelegramToken}/getUpdates");

        var fileText = harness.ReadLogFile();

        fileText.ShouldNotBeEmpty();
        fileText.ShouldNotContain(TelegramSecret);
        fileText.ShouldNotContain(TelegramToken);
        // AC2: surrounding message text is preserved, so the log line stays diagnostically useful.
        fileText.ShouldContain("Start processing HTTP request");
        fileText.ShouldContain("https://api.telegram.org/bot");
        fileText.ShouldContain(Redacted);
    }

    /// <summary>
    /// Non-vacuity control for AC4. The SAME event through the SAME sink configuration MINUS the
    /// redaction step leaks the token verbatim, which is what makes the assertion above load-bearing
    /// rather than a property of the harness or the message shape.
    /// </summary>
    [Fact]
    public void FileSink_WithoutRedactionStep_LeaksTelegramToken_ProvingTheAssertionIsNotVacuous()
    {
        using var harness = new HarnessBuilder().WithoutRedaction().Build();

        harness.CreateLogger<SerilogSecretRedactionTests>()
            .LogInformation(
                "Start processing HTTP request {Method} {Uri}",
                "POST",
                $"https://api.telegram.org/bot{TelegramToken}/getUpdates");

        harness.ReadLogFile().ShouldContain(TelegramSecret);
    }

    [Fact]
    public void FileSink_DoesNotWriteTelegramTokenEmbeddedInLiteralTemplateText()
    {
        using var harness = new HarnessBuilder().Build();

        // Literal template text, not a property - the other half of AC2/AC3.
#pragma warning disable CA2254 // Deliberately non-constant: the leak under test is literal text.
        harness.CreateLogger<SerilogSecretRedactionTests>()
            .LogInformation($"Sending HTTP request POST https://api.telegram.org/bot{TelegramToken}/getUpdates");
#pragma warning restore CA2254

        var fileText = harness.ReadLogFile();
        fileText.ShouldNotContain(TelegramSecret);
        fileText.ShouldContain(Redacted);
    }

    [Fact]
    public void RecentLogStore_ReceivesRedactedPropertyValue()
    {
        using var harness = new HarnessBuilder().Build();

        harness.CreateLogger<SerilogSecretRedactionTests>()
            .LogWarning("outbound {Uri}", $"https://api.telegram.org/bot{TelegramToken}/getUpdates");

        var entry = harness.Store.GetRecent(50).First(candidate => candidate.Message.Contains("outbound"));

        // AC3: the PROPERTY is redacted, not merely the rendered message. The recent-log store keeps
        // properties separately and serializes them as JSON for GET /api/logs/recent, so a
        // message-only fix would leak here.
        var uri = entry.Properties["Uri"]?.ToString();
        uri.ShouldNotBeNull();
        uri!.ShouldNotContain(TelegramSecret);
        uri.ShouldContain(Redacted);
        entry.Message.ShouldNotContain(TelegramSecret);
    }

    [Fact]
    public void JsonFormatter_OverRedactedEvent_EmitsNoTokenInStructuredProperties()
    {
        // AC3 stated directly against a JSON formatter: a structured serializer must not be able to
        // recover the token from the event's property graph.
        var logEvent = CreateEvent(
            "calling {Uri} with {Options}",
            new LogEventProperty("Uri", new ScalarValue($"https://api.telegram.org/bot{TelegramToken}/getUpdates")),
            new LogEventProperty("Options", new StructureValue(
            [
                new LogEventProperty("Token", new ScalarValue(TelegramToken)),
                new LogEventProperty("Retries", new ScalarValue(3))
            ])));

        var redacted = SecretRedactingSink.Redact(logEvent, new SecretRedactor());

        using var writer = new StringWriter();
        new JsonFormatter().Format(redacted, writer);
        var json = writer.ToString();

        json.ShouldNotContain(TelegramSecret);
        json.ShouldContain(Redacted);
        json.ShouldContain("Retries");
    }

    [Fact]
    public void SequenceAndDictionaryPropertyValues_AreRedacted()
    {
        var logEvent = CreateEvent(
            "batch {Urls} {Headers}",
            new LogEventProperty("Urls", new SequenceValue(
            [
                new ScalarValue("https://example.invalid/healthz"),
                new ScalarValue($"https://api.telegram.org/bot{TelegramToken}/getUpdates")
            ])),
            new LogEventProperty("Headers", new DictionaryValue(
            [
                new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                    new ScalarValue("x-telegram"),
                    new ScalarValue(TelegramToken))
            ])));

        var redacted = SecretRedactingSink.Redact(logEvent, new SecretRedactor());

        using var writer = new StringWriter();
        new JsonFormatter().Format(redacted, writer);
        var json = writer.ToString();

        json.ShouldNotContain(TelegramSecret);
        // The clean sibling element survives untouched, so redaction is targeted rather than a
        // blanket rewrite of the collection.
        json.ShouldContain("https://example.invalid/healthz");
    }

    [Fact]
    public void SecretFreeEvent_IsForwardedAsTheSameInstance_NoPerEventAllocation()
    {
        // AC5: the common case must not allocate. SecretRedactor.Redact returns its input unchanged
        // when nothing matches, and this asserts that the sink propagates that fast path all the way
        // out - the very same LogEvent reference is forwarded, so no template, property list or
        // event object is rebuilt.
        var logEvent = CreateEvent(
            "ordinary {Count} {Name}",
            new LogEventProperty("Count", new ScalarValue(7)),
            new LogEventProperty("Name", new ScalarValue("nothing secret here")));

        var result = SecretRedactingSink.Redact(logEvent, new SecretRedactor());

        ReferenceEquals(result, logEvent).ShouldBeTrue();
    }

    [Fact]
    public void DirtyEvent_IsRewritten_AndCleanSiblingPropertiesArePreserved()
    {
        var logEvent = CreateEvent(
            "mixed {Uri} {Attempt}",
            new LogEventProperty("Uri", new ScalarValue($"https://api.telegram.org/bot{TelegramToken}/getUpdates")),
            new LogEventProperty("Attempt", new ScalarValue(2)));

        var result = SecretRedactingSink.Redact(logEvent, new SecretRedactor());

        ReferenceEquals(result, logEvent).ShouldBeFalse();
        result.Properties.Count.ShouldBe(logEvent.Properties.Count);
        ((ScalarValue)result.Properties["Attempt"]).Value.ShouldBe(2);
        result.Properties["Uri"].ToString().ShouldContain(Redacted);
        result.Level.ShouldBe(logEvent.Level);
        result.Timestamp.ShouldBe(logEvent.Timestamp);
    }

    [Fact]
    public void BootstrapLogger_RedactsTelegramToken()
    {
        // AC1 extends to Program.cs's bootstrap logger, which runs before the DI container exists
        // and was previously configured with bare WriteTo.Console/WriteTo.File.
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .ConfigureBootstrapLogging(Path.Combine(directory, "botnexus-bootstrap-.log"))
                .CreateLogger())
            {
                logger.Information(
                    "bootstrap probe {Uri}",
                    $"https://api.telegram.org/bot{TelegramToken}/getUpdates");
            }

            var text = string.Concat(Directory.EnumerateFiles(directory).Select(File.ReadAllText));
            text.ShouldNotBeEmpty();
            text.ShouldNotContain(TelegramSecret);
            text.ShouldContain(Redacted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static LogEvent CreateEvent(string template, params LogEventProperty[] properties) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse(template),
            properties);

    // Builds a host container plus the SHIPPED gateway Serilog configuration, mirroring how
    // Program.cs hands Serilog ownership of the ILoggerFactory. WithoutRedaction() reproduces the
    // pre-fix wiring so the non-vacuity control exercises the identical path minus one step.
    private sealed class HarnessBuilder
    {
        private bool _redact = true;

        public HarnessBuilder WithoutRedaction()
        {
            _redact = false;
            return this;
        }

        public Harness Build() => new(_redact);
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _logDirectory;
        private readonly IHost _host;
        private readonly Logger _serilogLogger;
        private readonly ILoggerFactory _loggerFactory;

        public Harness(bool redact)
        {
            _logDirectory = Path.Combine(Path.GetTempPath(), "botnexus-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_logDirectory);

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddGatewayRecentLogStore();
            _host = builder.Build();

            var configuration = new LoggerConfiguration().MinimumLevel.Debug();

            _serilogLogger = redact
                ? configuration
                    .ConfigureGatewayLogging(builder.Configuration, _host.Services, _logDirectory)
                    .CreateLogger()
                : ConfigureWithoutRedaction(configuration).CreateLogger();

            _loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddSerilog(_serilogLogger);
            });
        }

        public IRecentLogStore Store => _host.Services.GetRequiredService<IRecentLogStore>();

        public ILogger<T> CreateLogger<T>() => _loggerFactory.CreateLogger<T>();

        // Flushes by disposing the logger first - Serilog's file sink buffers, so reading before
        // the flush would make an absence assertion pass for the wrong reason.
        public string ReadLogFile()
        {
            _loggerFactory.Dispose();
            _serilogLogger.Dispose();
            return string.Concat(Directory.EnumerateFiles(_logDirectory, "botnexus-*.log").Select(File.ReadAllText));
        }

        // The pre-#3276 sink configuration, kept ONLY as the non-vacuity control.
        private LoggerConfiguration ConfigureWithoutRedaction(LoggerConfiguration configuration) =>
            configuration
                .Enrich.FromLogContext()
                .WriteTo.Sink(new RecentLogStoreSink(Store))
                .WriteTo.File(
                    Path.Combine(_logDirectory, "botnexus-.log"),
                    rollingInterval: RollingInterval.Hour,
                    retainedFileCountLimit: 168);

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
