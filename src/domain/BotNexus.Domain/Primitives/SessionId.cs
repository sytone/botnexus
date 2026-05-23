using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a single LLM-context-bounded session inside a <see cref="ConversationId"/>.
/// Construct via <see cref="From(string)"/> for existing values, <see cref="Create"/> for a
/// new generic session, or one of the role-specific factories below for the structured-id
/// flavours (sub-agent, agent-agent, soul, cross-agent). The value must be non-null,
/// non-empty, non-whitespace and is stored trimmed.
/// </summary>
/// <remarks>
/// The structured factory outputs (<c>...::subagent::...</c>, <c>...::agent-agent::...</c>,
/// <c>...::soul::yyyy-MM-dd</c>, <c>xagent::...::...</c>) are part of the persisted wire
/// format and have format-pinning tests; do not change their shape without coordinated
/// migration.
/// </remarks>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct SessionId
{
    /// <summary>
    /// Creates a new unique <see cref="SessionId"/> using a 32-character N-formatted GUID.
    /// </summary>
    public static SessionId Create() => From(Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates the canonical sub-agent session id <c>{parent}::subagent::{uniqueId}</c>.
    /// </summary>
    public static SessionId ForSubAgent(string parentId, string uniqueId)
    {
        var parent = From(parentId);
        if (string.IsNullOrWhiteSpace(uniqueId))
            throw new ArgumentException("Sub-agent unique ID cannot be empty", nameof(uniqueId));

        return From($"{parent.Value}::subagent::{uniqueId.Trim()}");
    }

    /// <summary>
    /// Convenience overload of <see cref="ForSubAgent(string, string)"/> taking a
    /// typed parent <see cref="SessionId"/>.
    /// </summary>
    public static SessionId ForSubAgent(SessionId parentId, string uniqueId)
        => ForSubAgent(parentId.Value, uniqueId);

    /// <summary>
    /// Creates the canonical agent-to-agent session id
    /// <c>{initiator}::agent-agent::{target}::{uniqueId}</c>.
    /// </summary>
    public static SessionId ForAgentConversation(AgentId initiatorId, AgentId targetId, string uniqueId)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
            throw new ArgumentException("Conversation unique ID cannot be empty", nameof(uniqueId));

        return From($"{initiatorId.Value}::agent-agent::{targetId.Value}::{uniqueId.Trim()}");
    }

    /// <summary>
    /// Creates the canonical soul session id <c>{agent}::soul::yyyy-MM-dd</c>.
    /// </summary>
    public static SessionId ForSoul(AgentId agentId, DateOnly date)
        => From($"{agentId.Value}::soul::{date:yyyy-MM-dd}");

    /// <summary>
    /// Convenience overload that derives the UTC date from a <see cref="DateTimeOffset"/>.
    /// </summary>
    public static SessionId ForSoul(AgentId agentId, DateTimeOffset timestampUtc)
        => ForSoul(agentId, DateOnly.FromDateTime(timestampUtc.UtcDateTime));

    /// <summary>
    /// Creates the canonical cross-agent (legacy) session id
    /// <c>xagent::{source}::{target}</c>. New code should prefer
    /// <see cref="ForAgentConversation"/> backed by a real Conversation; this overload
    /// stays for back-compat reads.
    /// </summary>
    public static SessionId ForCrossAgent(string sourceId, string targetId)
    {
        var source = From(sourceId);
        var target = From(targetId);
        return From($"xagent::{source.Value}::{target.Value}");
    }

    /// <summary>
    /// True when this id matches the <see cref="ForSubAgent(string, string)"/> shape.
    /// </summary>
    public bool IsSubAgent => Value.Contains("::subagent::", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this id matches the <see cref="ForAgentConversation"/> shape.
    /// </summary>
    public bool IsAgentConversation => Value.Contains("::agent-agent::", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this id matches the <see cref="ForSoul(AgentId, DateOnly)"/> shape.
    /// </summary>
    public bool IsSoul => Value.Contains("::soul::", StringComparison.OrdinalIgnoreCase);

    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("SessionId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
