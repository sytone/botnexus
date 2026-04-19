using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(SessionIdJsonConverter))]
/// <summary>
/// Represents struct.
/// </summary>
public readonly record struct SessionId(string Value) : IComparable<SessionId>
{
    /// <summary>
    /// Executes from.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The from result.</returns>
    public static SessionId From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("SessionId cannot be empty", nameof(value))
            : new(value.Trim());

    /// <summary>
    /// Executes create.
    /// </summary>
    /// <returns>The create result.</returns>
    public static SessionId Create() => new(Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Executes for sub agent.
    /// </summary>
    /// <param name="parentId">The parent id.</param>
    /// <param name="uniqueId">The unique id.</param>
    /// <returns>The for sub agent result.</returns>
    public static SessionId ForSubAgent(string parentId, string uniqueId)
    {
        var parentSessionId = From(parentId);
        if (string.IsNullOrWhiteSpace(uniqueId))
            throw new ArgumentException("Sub-agent unique ID cannot be empty", nameof(uniqueId));

        return new($"{parentSessionId.Value}::subagent::{uniqueId.Trim()}");
    }

    /// <summary>
    /// Executes for sub agent.
    /// </summary>
    /// <param name="parentId">The parent id.</param>
    /// <param name="uniqueId">The unique id.</param>
    /// <returns>The for sub agent result.</returns>
    public static SessionId ForSubAgent(SessionId parentId, string uniqueId)
        => ForSubAgent(parentId.Value, uniqueId);

    /// <summary>
    /// Executes for agent conversation.
    /// </summary>
    /// <param name="initiatorId">The initiator id.</param>
    /// <param name="targetId">The target id.</param>
    /// <param name="uniqueId">The unique id.</param>
    /// <returns>The for agent conversation result.</returns>
    public static SessionId ForAgentConversation(AgentId initiatorId, AgentId targetId, string uniqueId)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
            throw new ArgumentException("Conversation unique ID cannot be empty", nameof(uniqueId));

        return new($"{initiatorId}::agent-agent::{targetId}::{uniqueId.Trim()}");
    }

    /// <summary>
    /// Executes for soul.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="date">The date.</param>
    /// <returns>The for soul result.</returns>
    public static SessionId ForSoul(AgentId agentId, DateOnly date)
    {
        return new($"{agentId.Value}::soul::{date:yyyy-MM-dd}");
    }

    /// <summary>
    /// Executes for soul.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="timestampUtc">The timestamp utc.</param>
    /// <returns>The for soul result.</returns>
    public static SessionId ForSoul(AgentId agentId, DateTimeOffset timestampUtc)
        => ForSoul(agentId, DateOnly.FromDateTime(timestampUtc.UtcDateTime));

    /// <summary>
    /// Executes for cross agent.
    /// </summary>
    /// <param name="sourceId">The source id.</param>
    /// <param name="targetId">The target id.</param>
    /// <returns>The for cross agent result.</returns>
    public static SessionId ForCrossAgent(string sourceId, string targetId)
    {
        var sourceSessionId = From(sourceId);
        var targetSessionId = From(targetId);
        return new($"xagent::{sourceSessionId.Value}::{targetSessionId.Value}");
    }

    public bool IsSubAgent => Value.Contains("::subagent::", StringComparison.OrdinalIgnoreCase);
    public bool IsAgentConversation => Value.Contains("::agent-agent::", StringComparison.OrdinalIgnoreCase);
    public bool IsSoul => Value.Contains("::soul::", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Performs the declared conversion or operator operation.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The operator string result.</returns>
    public static implicit operator string(SessionId id) => id.Value;
    /// <summary>
    /// Performs the declared conversion or operator operation.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The operator session id result.</returns>
    public static implicit operator SessionId(string value) => From(value);

    /// <summary>
    /// Executes to string.
    /// </summary>
    /// <returns>The to string result.</returns>
    public override string ToString() => Value;
    /// <summary>
    /// Executes compare to.
    /// </summary>
    /// <param name="other">The other.</param>
    /// <returns>The compare to result.</returns>
    public int CompareTo(SessionId other) => string.Compare(Value, other.Value, StringComparison.Ordinal);
}
