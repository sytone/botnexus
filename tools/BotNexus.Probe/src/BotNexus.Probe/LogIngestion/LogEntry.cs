namespace BotNexus.Probe.LogIngestion;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Exception,
    string? CorrelationId,
    string? SessionId,
    string? AgentId,
    string? Channel,
    string SourceFile,
    long LineNumber,
    IReadOnlyDictionary<string, string> Properties);
