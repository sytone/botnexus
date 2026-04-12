namespace BotNexus.Domain.World;

using BotNexus.Domain.Primitives;

public sealed record CrossWorldPermission
{
    public required string TargetWorldId { get; init; }
    public IReadOnlyList<AgentId>? AllowedAgents { get; init; }
    public bool AllowInbound { get; init; } = true;
    public bool AllowOutbound { get; init; } = true;
}
