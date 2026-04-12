namespace BotNexus.Domain.World;

using BotNexus.Domain.Primitives;

public sealed record WorldDescriptor
{
    public required WorldIdentity Identity { get; init; }
    public IReadOnlyList<AgentId> HostedAgents { get; init; } = [];
    public IReadOnlyList<Location> Locations { get; init; } = [];
    public IReadOnlyList<ExecutionStrategy> AvailableStrategies { get; init; } = [];
    public IReadOnlyList<CrossWorldPermission> CrossWorldPermissions { get; init; } = [];
}
