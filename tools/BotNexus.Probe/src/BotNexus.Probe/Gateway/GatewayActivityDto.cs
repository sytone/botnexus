namespace BotNexus.Probe.Gateway;

public sealed record GatewayActivityDto(
    string EventId,
    string Type,
    string? AgentId,
    string? SessionId,
    string? ChannelType,
    string? Message,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object?>? Data);
