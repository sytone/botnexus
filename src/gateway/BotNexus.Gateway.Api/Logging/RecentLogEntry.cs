namespace BotNexus.Gateway.Api.Logging;

/// <summary>
/// Represents recent log entry.
/// </summary>
public sealed record RecentLogEntry(
    DateTimeOffset Timestamp,
    string Category,
    string Level,
    string Message,
    string? Exception,
    IReadOnlyDictionary<string, object?> Properties);
