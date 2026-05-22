using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// An <see cref="ILoggerProvider"/> that captures all log entries to the
/// <see cref="TestChannelAdapter"/> log buffer, making them queryable via the
/// <c>/test-channel/logs</c> HTTP endpoint.
/// </summary>
public sealed class TestChannelLoggerProvider : ILoggerProvider
{
    private readonly TestChannelAdapter _adapter;

    public TestChannelLoggerProvider(TestChannelAdapter adapter)
    {
        _adapter = adapter;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new TestChannelLogger(categoryName, _adapter);

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>
/// Logger implementation that writes to the <see cref="TestChannelAdapter"/> log buffer.
/// </summary>
public sealed class TestChannelLogger : ILogger
{
    private readonly string _category;
    private readonly TestChannelAdapter _adapter;

    public TestChannelLogger(string category, TestChannelAdapter adapter)
    {
        _category = category;
        _adapter = adapter;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (exception is not null)
            message = $"{message} | Exception: {exception.Message}";

        var properties = new Dictionary<string, object?>
        {
            ["category"] = _category,
            ["eventId"] = eventId.Id,
            ["eventName"] = eventId.Name
        };

        // Extract structured log properties from IReadOnlyList<KeyValuePair<string, object?>> state
        if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
        {
            foreach (var (key, val) in kvps)
            {
                if (key != "{OriginalFormat}")
                    properties[key] = val;
            }
        }

        _adapter.CaptureLog(new TestLogEntry(
            Timestamp: DateTimeOffset.UtcNow,
            Level: logLevel.ToString(),
            Message: message,
            Properties: properties));
    }
}
