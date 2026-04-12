namespace BotNexus.Domain.Primitives;

public sealed record SessionParticipant
{
    public required ParticipantType Type { get; init; }
    public required string Id { get; init; }
    public string? WorldId { get; init; }
    public string? Role { get; init; }
}

public enum ParticipantType
{
    User,
    Agent
}
