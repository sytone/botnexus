using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Captures log records so a test can assert that a decision was RECORDED, not merely taken.
/// </summary>
/// <remarks>
/// #2682 requires that a Warn-mode trust failure is logged. That is not an incidental detail: Warn
/// exists precisely to make a tamper visible on a fleet that cannot yet fail closed, so a Warn that
/// permits silently is indistinguishable from Disabled. Asserting only the boolean outcome would
/// leave that difference untested.
/// </remarks>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    /// <summary>Every record written, in order.</summary>
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
