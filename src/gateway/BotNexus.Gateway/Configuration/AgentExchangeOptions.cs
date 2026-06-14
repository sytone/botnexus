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

    /// <summary>
    /// Upper bound applied to the <c>maxTurns</c> argument of a single <c>agent_converse</c>
    /// invocation. A single exchange runs at most this many back-and-forth turns regardless of
    /// the value an agent requests, which prevents one tool call from driving an unbounded number
    /// of provider round-trips (the conversation budget tracker only caps the number of exchanges
    /// per agent pair, not the turns within an exchange). Mirrors
    /// <c>SubAgentOptions.DefaultMaxTurns</c>. Values below 1 are treated as 1.
    /// </summary>
    public int MaxTurnsCeiling { get; set; } = 30;

    /// <summary>
    /// Returns the effective ceiling, guaranteed to be at least 1, so a misconfigured
    /// non-positive value never disables exchanges entirely.
    /// </summary>
    public int EffectiveMaxTurnsCeiling => Math.Max(1, MaxTurnsCeiling);
}
