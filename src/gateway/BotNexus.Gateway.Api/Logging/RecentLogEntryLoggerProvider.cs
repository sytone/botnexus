using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Logging;

/// <summary>
/// Represents recent log entry logger provider.
/// </summary>
public sealed class RecentLogEntryLoggerProvider(IRecentLogStore store) : ILoggerProvider
{
    /// <summary>
    /// Executes create logger.
    /// </summary>
    /// <param name="categoryName">The category name.</param>
    /// <returns>The create logger result.</returns>
    public ILogger CreateLogger(string categoryName) => new RecentLogEntryLogger(categoryName, store);

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
    }

    private sealed class RecentLogEntryLogger(string category, IRecentLogStore store) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        /// <summary>
        /// Executes is enabled.
        /// </summary>
        /// <param name="logLevel">The log level.</param>
        /// <returns>The is enabled result.</returns>
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter is null)
                return;

            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (state is IEnumerable<KeyValuePair<string, object?>> stateValues)
            {
                foreach (var pair in stateValues)
                {
                    if (string.Equals(pair.Key, "{OriginalFormat}", StringComparison.Ordinal))
                        continue;

                    properties[pair.Key] = pair.Value;
                }
            }

            if (eventId.Id != 0)
                properties["eventId"] = eventId.Id;

            store.Add(new RecentLogEntry(
                Timestamp: DateTimeOffset.UtcNow,
                Category: category,
                Level: logLevel.ToString(),
                Message: formatter(state, exception),
                Exception: exception?.ToString(),
                Properties: properties));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            /// <summary>
            /// Executes dispose.
            /// </summary>
            public void Dispose()
            {
            }
        }
    }
}
