namespace BotNexus.Gateway.Contracts.Memory;

/// <summary>
/// Creates <see cref="IAgentMemory"/> instances for a given agent, resolving the
/// appropriate provider based on agent configuration.
/// </summary>
public interface IAgentMemoryFactory
{
    /// <summary>
    /// Creates or retrieves a memory provider instance for the specified agent.
    /// The provider type is resolved from the agent's memory configuration
    /// (e.g. "markdown", "qmd", "hybrid").
    /// </summary>
    /// <param name="agentId">The agent identifier to create memory for.</param>
    /// <param name="providerName">
    /// Optional provider name override. When null, uses the agent's configured provider.
    /// </param>
    /// <returns>A configured <see cref="IAgentMemory"/> instance.</returns>
    IAgentMemory Create(string agentId, string? providerName = null);

    /// <summary>
    /// Returns the names of all registered memory providers.
    /// </summary>
    IReadOnlyList<string> GetRegisteredProviders();
}
