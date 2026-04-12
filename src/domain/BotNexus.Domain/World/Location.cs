namespace BotNexus.Domain.World;

using BotNexus.Domain.Primitives;

public sealed record Location
{
    public required string Name { get; init; }
    public required LocationType Type { get; init; }
    public string? Path { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}
