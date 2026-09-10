namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Per-circuit implementation of <see cref="IAgentPersonaLauncher"/>.
/// </summary>
/// <remarks>
/// Holds no state beyond the event, deliberately. It carries no "is open" flag and no "current
/// agent" because the panel itself owns both: a launcher that tracked them would be a second
/// source of truth, and the two would disagree the moment the user dismissed the panel by
/// clicking away.
/// </remarks>
public sealed class AgentPersonaLauncher : IAgentPersonaLauncher
{
    /// <inheritdoc />
    public event Action<string>? Requested;

    /// <inheritdoc />
    public void Request(string agentId)
    {
        if (!string.IsNullOrWhiteSpace(agentId))
            Requested?.Invoke(agentId);
    }
}
