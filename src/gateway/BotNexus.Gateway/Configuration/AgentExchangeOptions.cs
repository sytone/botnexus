namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Configuration for the agent-to-agent exchange system.
/// Bound from <c>gateway:agentExchange</c> in config.json.
/// </summary>
public sealed class AgentExchangeOptions
{
    /// <summary>
    /// Controls which agents are allowed to initiate conversations with other agents.
    /// <list type="bullet">
    /// <item><c>open</c> (default): any registered agent can converse with any other registered agent.</item>
    /// <item><c>whitelist</c>: the initiator must have the target in its <c>SubAgentIds</c> list
    /// or have a matching <c>SubAgentRoles</c> grant.</item>
    /// </list>
    /// </summary>
    public string AccessPolicy { get; set; } = "open";

    /// <summary>
    /// Returns <see langword="true"/> when <see cref="AccessPolicy"/> is <c>open</c> (case-insensitive).
    /// </summary>
    public bool IsOpen => string.Equals(AccessPolicy, "open", StringComparison.OrdinalIgnoreCase);
}
