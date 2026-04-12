using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Triggers;

/// <summary>
/// Internal trigger contract for non-channel session creation paths.
/// </summary>
public interface IInternalTrigger
{
    /// <summary>
    /// Gets the trigger type identifier.
    /// </summary>
    TriggerType Type { get; }

    /// <summary>
    /// Gets a human-readable display name for this trigger.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Creates and executes a session for this trigger source.
    /// </summary>
    Task<SessionId> CreateSessionAsync(AgentId agentId, string prompt, CancellationToken ct = default);
}
