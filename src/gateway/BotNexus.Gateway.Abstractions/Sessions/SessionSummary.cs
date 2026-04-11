using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

public sealed record SessionSummary(
    string SessionId,
    string AgentId,
    string? ChannelType,
    SessionStatus Status,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
