using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Custom ILoggerProvider that captures Warning+ log entries into a <see cref="LogDiagnosticsRingBuffer"/>.
/// </summary>
[ProviderAlias("LogDiagnostics")]
public sealed class LogDiagnosticsProvider : ILoggerProvider
{
    private readonly LogDiagnosticsRingBuffer _buffer;

    public LogDiagnosticsProvider(LogDiagnosticsRingBuffer buffer)
    {
        _buffer = buffer;
    }

    public ILogger CreateLogger(string categoryName) => new LogDiagnosticsLogger(_buffer);

    public void Dispose()
    {
        // No resources to release — ring buffer is managed by DI container.
    }
}

internal sealed class LogDiagnosticsLogger : ILogger
{
    private readonly LogDiagnosticsRingBuffer _buffer;

    public LogDiagnosticsLogger(LogDiagnosticsRingBuffer buffer)
    {
        _buffer = buffer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var renderedMessage = formatter(state, exception);

        // Try to extract the original format string (message template) from the state.
        string? messageTemplate = null;
        if (state is IReadOnlyList<KeyValuePair<string, object?>> logValues)
        {
            foreach (var kvp in logValues)
            {
                if (kvp.Key == "{OriginalFormat}")
                {
                    messageTemplate = kvp.Value?.ToString();
                    break;
                }
            }
        }

        _buffer.Record(logLevel, messageTemplate, renderedMessage);
    }
}
