using BotNexus.Gateway.Abstractions.Models;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
using SessionType = BotNexus.Domain.Primitives.SessionType;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Represents session summary.
/// </summary>
public sealed record SessionSummary(
    string SessionId,
    string AgentId,
    ChannelKey? ChannelType,
    SessionStatus Status,
    SessionType SessionType,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
