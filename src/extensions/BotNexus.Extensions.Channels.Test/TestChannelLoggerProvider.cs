using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// An <see cref="ILoggerProvider"/> that mirrors every gateway log entry into a bounded in-memory
/// buffer so a test can assert on structured log output over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// This provider is ADDITIVE — it is appended to the host's existing providers rather than
/// replacing them, so console/file logging is unaffected while the gateway is under test.
/// </para>
/// <para>
/// It is registered only by <see cref="TestChannelServiceCollectionExtensions.AddBotNexusTestChannel"/>,
/// which itself only runs when the opt-in extension is loaded. A production configuration never
/// reaches this type.
/// </para>
/// </remarks>
public sealed class TestChannelLoggerProvider : ILoggerProvider
{
    private readonly TestChannelLogCapture _capture;
    private readonly ConcurrentDictionary<string, TestChannelLogger> _loggers = new(StringComparer.Ordinal);

    /// <summary>Creates the provider over a shared capture buffer.</summary>
    /// <param name="capture">The buffer entries are written to.</param>
    public TestChannelLoggerProvider(TestChannelLogCapture capture) => _capture = capture;

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new TestChannelLogger(name, _capture));

    /// <inheritdoc/>
    public void Dispose() => _loggers.Clear();

    private sealed class TestChannelLogger : ILogger
    {
        private readonly string _category;
        private readonly TestChannelLogCapture _capture;

        public TestChannelLogger(string category, TestChannelLogCapture capture)
        {
            _category = category;
            _capture = capture;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            ArgumentNullException.ThrowIfNull(formatter);

            // Structured state is flattened here rather than at read time so an assertion can target
            // a named property (botnexus.channel.type) instead of the rendered message text, which
            // changes whenever someone rewords a log line.
            var properties = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                foreach (var pair in structured)
                    properties[pair.Key] = pair.Value?.ToString();
            }

            _capture.Add(new TestChannelLogEntry(
                DateTimeOffset.UtcNow,
                logLevel.ToString(),
                _category,
                formatter(state, exception),
                exception?.ToString(),
                properties));
        }
    }
}
