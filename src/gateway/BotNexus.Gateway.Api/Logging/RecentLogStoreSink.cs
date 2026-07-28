using Serilog.Core;
using Serilog.Events;

namespace BotNexus.Gateway.Api.Logging;

/// <summary>
/// Serilog sink that mirrors every emitted log event into the in-process
/// <see cref="IRecentLogStore"/> that backs <c>GET /api/logs/recent</c>.
/// <para>
/// Serilog's host integration replaces the <see cref="Microsoft.Extensions.Logging.ILoggerFactory"/>
/// wholesale, so a DI-registered <c>ILoggerProvider</c> is never attached and the store stayed empty
/// (issue #2390). Participating as a Serilog sink keeps Serilog as the single logging pipeline while
/// still populating the diagnostics buffer that the API exposes.
/// </para>
/// </summary>
public sealed class RecentLogStoreSink(IRecentLogStore store) : ILogEventSink
{
    private const string SourceContextProperty = "SourceContext";

    /// <summary>
    /// Converts the Serilog event into a <see cref="RecentLogEntry"/> and appends it to the store.
    /// Runs on the logging hot path, so it performs no I/O beyond the bounded in-memory append.
    /// </summary>
    /// <param name="logEvent">The event emitted by the Serilog pipeline.</param>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null)
            return;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in logEvent.Properties)
        {
            if (string.Equals(property.Key, SourceContextProperty, StringComparison.Ordinal))
                continue;

            properties[property.Key] = RenderPropertyValue(property.Value);
        }

        var category = logEvent.Properties.TryGetValue(SourceContextProperty, out var sourceContext)
            && sourceContext is ScalarValue { Value: string contextName }
                ? contextName
                : "BotNexus";

        store.Add(new RecentLogEntry(
            Timestamp: logEvent.Timestamp,
            Category: category,
            Level: MapLevel(logEvent.Level),
            Message: logEvent.RenderMessage(),
            Exception: logEvent.Exception?.ToString(),
            Properties: properties));
    }

    // Scalars are unwrapped so the JSON payload carries real numbers/strings rather than
    // Serilog's quoted rendering; structured values fall back to their rendered form.
    private static object? RenderPropertyValue(LogEventPropertyValue value) =>
        value is ScalarValue scalar ? scalar.Value : value.ToString();

    // Map to Microsoft.Extensions.Logging level names so API consumers see a single vocabulary
    // regardless of which pipeline produced the entry.
    private static string MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "Trace",
        LogEventLevel.Debug => "Debug",
        LogEventLevel.Information => "Information",
        LogEventLevel.Warning => "Warning",
        LogEventLevel.Error => "Error",
        LogEventLevel.Fatal => "Critical",
        _ => level.ToString()
    };
}
