namespace BotNexus.Domain.World;

using BotNexus.Domain.Primitives;

/// <summary>
/// Represents world descriptor.
/// </summary>
public sealed record WorldDescriptor
{
    /// <summary>
    /// Gets or sets the identity.
    /// </summary>
    public required WorldIdentity Identity { get; init; }
    /// <summary>
    /// Gets or sets the hosted agents.
    /// </summary>
    public IReadOnlyList<AgentId> HostedAgents { get; init; } = [];

    /// <summary>
    /// The users that are citizens of this world. Default empty until the inbound pipeline
    /// (Phase 2c) starts resolving sender identities and the registry (Phase 2a) is populated
    /// from configuration.
    /// </summary>
    public IReadOnlyList<UserId> HostedUsers { get; init; } = [];
    /// <summary>
    /// Gets or sets the locations.
    /// </summary>
    public IReadOnlyList<Location> Locations { get; init; } = [];
    /// <summary>
    /// Gets or sets the available strategies.
    /// </summary>
    public IReadOnlyList<ExecutionStrategy> AvailableStrategies { get; init; } = [];
    /// <summary>
    /// Gets or sets the cross world permissions.
    /// </summary>
    public IReadOnlyList<CrossWorldPermission> CrossWorldPermissions { get; init; } = [];
}
