namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// #3064: in-memory, per-circuit implementation of <see cref="IConversationMruService"/>.
/// </summary>
/// <remarks>
/// A <see cref="List{T}"/> per agent rather than a dictionary or linked list: the list is bounded to
/// <see cref="MaxEntriesPerAgent"/>, so promote/evict are trivially correct at this size and the
/// most-recent-first ordering is directly readable rather than reconstructed. Ordinal comparison
/// throughout - agent and conversation ids are opaque server-minted tokens, and case-insensitive
/// matching would silently collapse two genuinely distinct conversations into one entry.
/// </remarks>
public sealed class ConversationMruService : IConversationMruService
{
    /// <summary>
    /// Upper bound on remembered conversations per agent. The MRU only ever needs its head to answer
    /// the redirect and its second entry to answer a delete, so an unbounded list would be a pure
    /// leak on a long-lived circuit that fans across many conversations.
    /// </summary>
    public const int MaxEntriesPerAgent = 20;

    private readonly Dictionary<string, List<string>> _byAgent = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Record(string agentId, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(conversationId))
            return;

        if (!_byAgent.TryGetValue(agentId, out var entries))
        {
            entries = [];
            _byAgent[agentId] = entries;
        }

        entries.RemoveAll(id => string.Equals(id, conversationId, StringComparison.Ordinal));
        entries.Insert(0, conversationId);

        if (entries.Count > MaxEntriesPerAgent)
            entries.RemoveRange(MaxEntriesPerAgent, entries.Count - MaxEntriesPerAgent);
    }

    /// <inheritdoc />
    public string? GetMostRecent(string agentId) =>
        !string.IsNullOrWhiteSpace(agentId) && _byAgent.TryGetValue(agentId, out var entries) && entries.Count > 0
            ? entries[0]
            : null;

    /// <inheritdoc />
    public IReadOnlyList<string> GetForAgent(string agentId) =>
        !string.IsNullOrWhiteSpace(agentId) && _byAgent.TryGetValue(agentId, out var entries)
            ? entries.ToArray()
            : [];

    /// <inheritdoc />
    public void Remove(string agentId, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(conversationId))
            return;

        if (_byAgent.TryGetValue(agentId, out var entries))
            entries.RemoveAll(id => string.Equals(id, conversationId, StringComparison.Ordinal));
    }
}
