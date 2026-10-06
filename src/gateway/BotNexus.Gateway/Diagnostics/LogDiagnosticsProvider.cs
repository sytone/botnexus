using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>Captures bounded Warning+ structured diagnostics from all logger categories.</summary>
[ProviderAlias("LogDiagnostics")]
public sealed class LogDiagnosticsProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly LogDiagnosticsRingBuffer _buffer;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public LogDiagnosticsProvider(LogDiagnosticsRingBuffer buffer)
    {
        _buffer = buffer;
    }

    public ILogger CreateLogger(string categoryName) =>
        new LogDiagnosticsLogger(_buffer, categoryName, () => _scopeProvider);

    /// <summary>Receives the logging pipeline scope provider shared by all categories.</summary>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopeProvider = scopeProvider ?? new LoggerExternalScopeProvider();

    public void Dispose()
    {
    }
}

internal sealed class LogDiagnosticsLogger : ILogger
{
    private const string RedactedValue = "[REDACTED]";
    private static readonly string[] SensitiveKeyFragments =
    [
        "secret", "password", "passwd", "credential", "token", "authorization", "apikey",
        "api_key", "prompt", "argument", "result", "content", "message", "stack", "url", "uri"
    ];

    private readonly LogDiagnosticsRingBuffer _buffer;
    private readonly string _category;
    private readonly Func<IExternalScopeProvider> _scopeProvider;

    public LogDiagnosticsLogger(
        LogDiagnosticsRingBuffer buffer,
        string category,
        Func<IExternalScopeProvider> scopeProvider)
    {
        _buffer = buffer;
        _category = category;
        _scopeProvider = scopeProvider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        _scopeProvider().Push(state);

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

        var properties = new Dictionary<string, string?>(StringComparer.Ordinal);
        _scopeProvider().ForEachScope(static (scope, destination) => CaptureValues(scope, destination), properties);
        CaptureValues(state, properties);

        var originalFormat = ExtractOriginalFormat(state);
        var capturedTemplate = LogDiagnosticsRingBuffer.CaptureTemplate(originalFormat);
        var safeRenderedMessage = BuildSafeRenderedMessage(capturedTemplate, properties);
        _buffer.Record(logLevel, _category, eventId, originalFormat, safeRenderedMessage, properties);
    }

    private static void CaptureValues<TState>(TState state, Dictionary<string, string?> destination)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values)
            return;

        foreach (var pair in values)
        {
            if (pair.Key == "{OriginalFormat}" || IsSensitiveKey(pair.Key) ||
                !LogDiagnosticsRingBuffer.TryGetCanonicalPropertyName(pair.Key, out var canonicalName))
                continue;

            destination[canonicalName] = pair.Value is null
                ? null
                : LogDiagnosticsRingBuffer.CaptureValue(
                    pair.Value.ToString() ?? string.Empty,
                    LogDiagnosticsRingBuffer.MaxPropertyValueLength);
        }
    }

    private static string? ExtractOriginalFormat<TState>(TState state)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var pair in values)
            {
                if (pair.Key == "{OriginalFormat}")
                    return pair.Value?.ToString();
            }
        }

        return null;
    }

    private static string BuildSafeRenderedMessage(
        string template,
        IReadOnlyDictionary<string, string?> properties)
    {
        if (template is LogDiagnosticsRingBuffer.RedactedUnstructuredValue or
            LogDiagnosticsRingBuffer.RedactedOversizedValue)
            return template;

        var rendered = template;
        foreach (var pair in properties)
        {
            if (rendered.Length > LogDiagnosticsRingBuffer.MaxRenderedMessageLength)
                return LogDiagnosticsRingBuffer.RedactedOversizedValue;

            rendered = rendered.Replace($"{{{pair.Key}}}", pair.Value ?? "null", StringComparison.Ordinal);
        }

        var start = 0;
        while ((start = rendered.IndexOf('{', start)) >= 0)
        {
            var end = rendered.IndexOf('}', start + 1);
            if (end < 0)
                break;
            rendered = string.Concat(rendered.AsSpan(0, start), RedactedValue, rendered.AsSpan(end + 1));
            start += RedactedValue.Length;
        }

        return LogDiagnosticsRingBuffer.CaptureValue(
            rendered,
            LogDiagnosticsRingBuffer.MaxRenderedMessageLength);
    }

    private static bool IsSensitiveKey(string key) =>
        SensitiveKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
