using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a single LLM-context-bounded session inside a <see cref="ConversationId"/>.
/// Construct via <see cref="From(string)"/> for existing values, <see cref="Create"/> for a
/// new generic session, or one of the role-specific factories below for the structured-id
/// flavours (sub-agent, soul). The value must be non-null, non-empty, non-whitespace and is
/// stored trimmed.
/// </summary>
/// <remarks>
/// <para>
/// The structured factory outputs (<c>...::subagent::...</c>, <c>...::soul::yyyy-MM-dd</c>)
/// are part of the persisted wire format and have format-pinning tests; do not change their
/// shape without coordinated migration.
/// </para>
/// <para>
/// The <c>::agent-agent::</c> shape — previously minted by a <c>ForAgentConversation</c>
/// factory — was removed in Phase 4 / 1b (PR #548 sender, this PR receiver). Named↔named
/// and cross-world agent exchanges now flow through <c>IConversationStore</c> with generic
/// <see cref="Create"/> session ids. The <see cref="IsAgentConversation"/> predicate is
/// retained because <c>SessionStoreBase.InferSessionType</c> still reads it to bucket
/// pre-migration sessions persisted with the legacy encoding.
/// </para>
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
    /// True when this id matches the <see cref="ForSubAgent(string, string)"/> shape.
    /// </summary>
    public bool IsSubAgent => Value.Contains("::subagent::", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this id matches the pre-Phase-4 / 1b agent-to-agent encoding
    /// (<c>{initiator}::agent-agent::{target}::{uniqueId}</c>). Retained for
    /// <c>SessionStoreBase.InferSessionType</c> so legacy persisted sessions are still
    /// bucketed as <c>AgentAgent</c> on read. New sessions never have this shape.
    /// </summary>
    public bool IsAgentConversation => Value.Contains("::agent-agent::", StringComparison.OrdinalIgnoreCase);

    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("SessionId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
