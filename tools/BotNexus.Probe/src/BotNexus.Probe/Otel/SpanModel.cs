namespace BotNexus.Probe.Otel;

public sealed record SpanModel(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string ServiceName,
    string OperationName,
    DateTimeOffset StartTime,
    TimeSpan Duration,
    string Status,
    IReadOnlyDictionary<string, string> Attributes);
