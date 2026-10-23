using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Sessions;

public sealed partial class InMemorySessionStore
{
    /// <inheritdoc />
    public override async Task<SessionSummaryPage> ListSummaryPageAsync(SessionSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        List<SessionSummary> snapshots;
        lock (_sync) snapshots = _sessions.Values.Select(SessionSummary.FromSession).ToList();
        var summaries = new List<SessionSummary>();
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summary = snapshot;
            if (_conversationStore is not null)
            {
                // Summary projections may omit a binding; the aggregate binding remains non-nullable.
                var projectedBinding = summary.ConversationId;
                if (projectedBinding is null) continue;
                var ownership = await _conversationStore.GetOwnershipAsync(
                    ConversationId.From(projectedBinding), cancellationToken).ConfigureAwait(false);
                if (ownership is null) continue;
                summary = summary with { AgentId = ownership.AgentId.Value };
            }
            if (query.Matches(summary)) summaries.Add(summary);
        }
        return SessionSummaryWindow.ApplyQuery(summaries, query);
    }

    /// <inheritdoc />
    public override async Task<SessionHistoryScanPage> ScanHistoryAsync(SessionId sessionId, SessionHistoryScan scan,
        CancellationToken cancellationToken = default)
    {
        scan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        GatewaySession session;
        ConversationId binding;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var found)) return new([], true, 0);
            session = found;
            binding = session.ConversationId;
        }
        if (_conversationStore is not null)
        {
            if (!binding.IsInitialized()) throw new UnauthorizedAccessException("Session owner changed.");
            var ownership = await _conversationStore.GetOwnershipAsync(binding, cancellationToken).ConfigureAwait(false);
            if (ownership?.AgentId != scan.ExpectedAgentId) throw new UnauthorizedAccessException("Session owner changed.");
        }
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sessions.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, session)
                || !ConversationIdsMatch(binding, session.ConversationId))
                throw new UnauthorizedAccessException("Session binding changed.");
            if (_conversationStore is null && session.AgentId != scan.ExpectedAgentId)
                throw new UnauthorizedAccessException("Session owner changed.");
            var entries = new List<SessionEntry>();
            var characters = 0;
            for (var i = 0; i < session.MessageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count == scan.MaxEntries) return new(entries, false, characters);
                var entry = session.GetHistorySnapshot(i, 1).FirstOrDefault();
                if (entry is null) break;
                if (entry.Content.Length > scan.MaxCharacters - characters) return new(entries, false, characters);
                characters += entry.Content.Length;
                // Search needs no replay, thinking, tool arguments or other potentially huge fields.
                entries.Add(new SessionEntry { Role = entry.Role, Content = entry.Content, Timestamp = entry.Timestamp });
            }
            return new(entries, true, characters);
        }
    }
}
