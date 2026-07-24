using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// In-memory <see cref="IConversationSectionStore"/> for development and testing (issue #2124).
/// Not durable - all sections and assignments are lost on restart. Thread-safe via
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> and a per-agent lock for the multi-row reorder
/// and next-order allocation.
/// </summary>
public sealed class InMemoryConversationSectionStore : IConversationSectionStore
{
    private readonly ConcurrentDictionary<string, ConversationSection> _sections = new(StringComparer.Ordinal);
    // conversationId -> sectionId. A conversation appears at most once, enforcing at-most-one-section.
    private readonly ConcurrentDictionary<string, string> _assignments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentLocks = new(StringComparer.Ordinal);
    private readonly IWorldContext? _worldContext;

    /// <summary>Initialises a new store without world stamping (tests and bare wire-ups).</summary>
    public InMemoryConversationSectionStore() { }

    /// <summary>Initialises a new store that stamps the current world id on persisted sections.</summary>
    /// <param name="worldContext">Resolves the gateway's current world identity for stamping.</param>
    public InMemoryConversationSectionStore(IWorldContext worldContext) => _worldContext = worldContext;

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationSection>> ListSectionsAsync(AgentId agentId, CancellationToken ct = default)
    {
        IReadOnlyList<ConversationSection> results = _sections.Values
            .Where(s => s.AgentId == agentId)
            .OrderBy(s => s.Order)
            .ThenBy(s => s.CreatedAt)
            .Select(Clone)
            .ToList();
        return Task.FromResult(results);
    }

    /// <inheritdoc />
    public Task<ConversationSection?> GetSectionAsync(SectionId sectionId, CancellationToken ct = default)
        => Task.FromResult(_sections.TryGetValue(sectionId.Value, out var s) ? Clone(s) : null);

    /// <inheritdoc />
    public async Task<ConversationSection> CreateSectionAsync(ConversationSection section, CancellationToken ct = default)
    {
        var gate = GetAgentLock(section.AgentId.Value);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StampWorldId(section);
            var nextOrder = _sections.Values.Where(s => s.AgentId == section.AgentId)
                .Select(s => s.Order)
                .DefaultIfEmpty(-1)
                .Max() + 1;
            section.Order = nextOrder;
            section.CreatedAt = DateTimeOffset.UtcNow;
            section.UpdatedAt = section.CreatedAt;
            if (!_sections.TryAdd(section.SectionId.Value, section))
                throw new InvalidOperationException($"A section with id '{section.SectionId}' already exists.");
            return Clone(section);
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public Task<ConversationSection?> UpdateSectionAsync(SectionId sectionId, string? name, bool? isCollapsed, CancellationToken ct = default)
    {
        if (!_sections.TryGetValue(sectionId.Value, out var existing))
            return Task.FromResult<ConversationSection?>(null);

        if (name is not null)
            existing.Name = name;
        if (isCollapsed is not null)
            existing.IsCollapsed = isCollapsed.Value;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult<ConversationSection?>(Clone(existing));
    }

    /// <inheritdoc />
    public async Task ReorderSectionsAsync(AgentId agentId, IReadOnlyList<SectionId> orderedSectionIds, CancellationToken ct = default)
    {
        var gate = GetAgentLock(agentId.Value);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var owned = _sections.Values.Where(s => s.AgentId == agentId).ToList();
            var ownedIds = owned.Select(s => s.SectionId.Value).ToHashSet(StringComparer.Ordinal);
            var order = 0;
            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in orderedSectionIds)
            {
                if (!ownedIds.Contains(id.Value) || !placed.Add(id.Value))
                    continue;
                _sections[id.Value].Order = order++;
                _sections[id.Value].UpdatedAt = DateTimeOffset.UtcNow;
            }
            // Any owned section omitted from the supplied order keeps its relative order after the
            // supplied ones (stable by prior Order then CreatedAt).
            foreach (var s in owned.Where(s => !placed.Contains(s.SectionId.Value)).OrderBy(s => s.Order).ThenBy(s => s.CreatedAt))
            {
                s.Order = order++;
                s.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public Task DeleteSectionAsync(SectionId sectionId, CancellationToken ct = default)
    {
        _sections.TryRemove(sectionId.Value, out _);
        // Remove assignments pointing at the deleted section - returns those conversations to their
        // system section without touching the conversations themselves.
        foreach (var kv in _assignments.Where(a => string.Equals(a.Value, sectionId.Value, StringComparison.Ordinal)).ToList())
            _assignments.TryRemove(kv.Key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AssignConversationAsync(SectionId sectionId, ConversationId conversationId, CancellationToken ct = default)
    {
        if (!_sections.ContainsKey(sectionId.Value))
            throw new InvalidOperationException($"Section '{sectionId}' does not exist.");
        // Upsert enforces at-most-one-section: overwrite any prior assignment.
        _assignments[conversationId.Value] = sectionId.Value;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveConversationAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        _assignments.TryRemove(conversationId.Value, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> GetAssignmentsAsync(AgentId agentId, CancellationToken ct = default)
    {
        var sectionIds = _sections.Values.Where(s => s.AgentId == agentId)
            .Select(s => s.SectionId.Value)
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> map = _assignments
            .Where(a => sectionIds.Contains(a.Value))
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        return Task.FromResult(map);
    }

    private SemaphoreSlim GetAgentLock(string agentId)
        => _agentLocks.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));

    private void StampWorldId(ConversationSection section)
    {
        if (string.IsNullOrEmpty(section.WorldId) && _worldContext is not null)
            section.WorldId = _worldContext.CurrentWorldId;
    }

    private static ConversationSection Clone(ConversationSection s) => s with { };
}
