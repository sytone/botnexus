namespace BotNexus.CodingAgent.Session;

public sealed record SessionInfo(
    string Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? Model,
    string WorkingDirectory);
