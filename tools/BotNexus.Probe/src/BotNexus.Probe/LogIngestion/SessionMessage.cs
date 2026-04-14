using System.Text.Json;

namespace BotNexus.Probe.LogIngestion;

public sealed record SessionMessage(
    DateTimeOffset? Timestamp,
    string? Role,
    string? Content,
    string? AgentId,
    string SessionId,
    IReadOnlyDictionary<string, JsonElement>? Metadata);
