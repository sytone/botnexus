namespace BotNexus.Core.Abstractions;

/// <summary>
/// Creates agent runners for a specific agent name.
/// </summary>
public interface IAgentRunnerFactory
{
    /// <summary>
    /// Creates an <see cref="IAgentRunner"/> for the supplied agent name.
    /// </summary>
    IAgentRunner Create(string agentName);
}
