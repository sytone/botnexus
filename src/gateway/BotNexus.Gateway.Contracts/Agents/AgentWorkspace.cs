namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Represents the workspace files used when composing agent context.
/// </summary>
public sealed record AgentWorkspace
{
    /// <summary>
    /// Initializes a new workspace snapshot.
    /// </summary>
    /// <param name="AgentName">The agent identifier associated with this workspace.</param>
    /// <param name="Soul">The content from <c>SOUL.md</c>, if present.</param>
    /// <param name="Identity">The content from <c>IDENTITY.md</c>, if present.</param>
    /// <param name="User">The content from <c>USER.md</c>, if present.</param>
    /// <param name="Memory">The content from <c>MEMORY.md</c>, if present.</param>
    public AgentWorkspace(string AgentName, string? Soul, string? Identity, string? User, string? Memory)
    {
        this.AgentName = AgentName;
        this.Soul = Soul;
        this.Identity = Identity;
        this.User = User;
        this.Memory = Memory;
    }

    /// <summary>The agent identifier associated with this workspace.</summary>
    public string AgentName { get; init; }

    /// <summary>The content from <c>SOUL.md</c>, if present.</summary>
    public string? Soul { get; init; }

    /// <summary>The content from <c>IDENTITY.md</c>, if present.</summary>
    public string? Identity { get; init; }

    /// <summary>The content from <c>USER.md</c>, if present.</summary>
    public string? User { get; init; }

    /// <summary>The content from <c>MEMORY.md</c>, if present.</summary>
    public string? Memory { get; init; }
}
