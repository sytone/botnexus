using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(AgentSessionKeyJsonConverter))]
/// <summary>
/// Represents struct.
/// </summary>
public readonly record struct AgentSessionKey(AgentId AgentId, SessionId SessionId)
{
    /// <summary>
    /// Executes from.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The from result.</returns>
    public static AgentSessionKey From(AgentId agentId, SessionId sessionId) => new(agentId, sessionId);

    /// <summary>
    /// Executes parse.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The parse result.</returns>
    public static AgentSessionKey Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("AgentSessionKey cannot be empty", nameof(value));

        var parts = value.Split("::", StringSplitOptions.None);
        if (parts.Length < 2)
            throw new ArgumentException("AgentSessionKey must be in '{agentId}::{sessionId}' format.", nameof(value));

        return new AgentSessionKey(
            AgentId.From(parts[0]),
            SessionId.From(string.Join("::", parts.Skip(1))));
    }

    /// <summary>
    /// Executes to string.
    /// </summary>
    /// <returns>The to string result.</returns>
    public override string ToString() => $"{AgentId}::{SessionId}";
}
