namespace BotNexus.Domain;

public sealed record WorldIdentity
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Emoji { get; init; }
}
