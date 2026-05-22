namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// A captured log entry from the test channel log sink.
/// </summary>
public sealed record TestLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    Dictionary<string, object?> Properties);
