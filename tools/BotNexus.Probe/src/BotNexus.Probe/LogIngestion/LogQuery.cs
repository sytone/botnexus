namespace BotNexus.Probe.LogIngestion;

public sealed record LogQuery(
    string? Level = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? CorrelationId = null,
    string? SessionId = null,
    string? AgentId = null,
    string? SearchText = null);
