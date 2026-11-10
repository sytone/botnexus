using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// Non-durable section store. One store gate serializes reads and all related mutations, including
/// assignments moving between agents, so callers never observe a partially updated group order.
/// </summary>
public sealed class InMemoryConversationSectionStore : IConversationSectionStore
{
    private readonly Dictionary<SectionId, ConversationSection> _sections = [];
    private readonly Dictionary<string, string> _assignments = new(StringComparer.Ordinal);
    private readonly Dictionary<AgentId, IReadOnlyList<ConversationSectionOrderEntry>> _groupOrders = [];
    private readonly object _gate = new();
    private readonly IWorldContext? _worldContext;

    /// <summary>Initialises a store without world stamping.</summary>
    public InMemoryConversationSectionStore() { }

    /// <summary>Initialises a store that stamps the resolved world identity.</summary>
    public InMemoryConversationSectionStore(IWorldContext worldContext) => _worldContext = worldContext;

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationSection>> ListSectionsAsync(AgentId agentId, CancellationToken ct = default)
        => Locked<IReadOnlyList<ConversationSection>>(() => Owned(agentId).Select(s => s with { }).ToArray(), ct);

    /// <inheritdoc />
    public Task<ConversationSection?> GetSectionAsync(SectionId sectionId, CancellationToken ct = default)
        => Locked(() => _sections.TryGetValue(sectionId, out var s) ? s with { } : null, ct);

    /// <inheritdoc />
    public Task<ConversationSection> CreateSectionAsync(ConversationSection section, CancellationToken ct = default)
        => Locked(() =>
        {
            if (_sections.ContainsKey(section.SectionId))
                throw new InvalidOperationException($"A section with id '{section.SectionId}' already exists.");
            var order = CurrentOrder(section.AgentId);
            var now = DateTimeOffset.UtcNow;
            var created = section with
            {
                Order = order.Count(e => e.SectionId.HasValue),
                CreatedAt = now,
                UpdatedAt = now,
                WorldId = string.IsNullOrEmpty(section.WorldId) ? _worldContext?.CurrentWorldId ?? string.Empty : section.WorldId
            };
            _sections.Add(created.SectionId, created);
            SaveOrder(created.AgentId, order.Append(ConversationSectionOrderEntry.ForSection(created.SectionId)).ToArray());
            return created with { };
        }, ct);

    /// <inheritdoc />
    public Task<ConversationSection?> UpdateSectionAsync(SectionId sectionId, string? name, bool? isCollapsed, CancellationToken ct = default)
        => Locked<ConversationSection?>(() =>
        {
            if (!_sections.TryGetValue(sectionId, out var existing))
                return null;
            var updated = existing with { Name = name ?? existing.Name, IsCollapsed = isCollapsed ?? existing.IsCollapsed, UpdatedAt = DateTimeOffset.UtcNow };
            _sections[sectionId] = updated;
            return updated with { };
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationSectionOrderEntry>> GetGroupOrderAsync(AgentId agentId, CancellationToken ct = default)
        => Locked(() => CurrentOrder(agentId), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationSectionOrderEntry>> ReorderGroupsAsync(AgentId agentId,
        IReadOnlyList<ConversationSectionOrderEntry> orderedGroups, CancellationToken ct = default)
        => Locked(() =>
        {
            var order = ConversationSectionOrderEntry.Reorder(CurrentOrder(agentId), orderedGroups);
            SaveOrder(agentId, order);
            return order;
        }, ct);

    /// <inheritdoc />
    public Task ReorderSectionsAsync(AgentId agentId, IReadOnlyList<SectionId> orderedSectionIds, CancellationToken ct = default)
        => Locked(() => SaveOrder(agentId, ConversationSectionOrderEntry.ReorderCustomSlots(CurrentOrder(agentId), orderedSectionIds)), ct);

    /// <inheritdoc />
    public Task DeleteSectionAsync(SectionId sectionId, CancellationToken ct = default)
        => Locked(() =>
        {
            if (!_sections.TryGetValue(sectionId, out var section))
                return;
            var order = CurrentOrder(section.AgentId).Where(e => e.SectionId != sectionId).ToArray();
            _sections.Remove(sectionId);
            foreach (var id in _assignments.Where(a => a.Value == sectionId.Value).Select(a => a.Key).ToArray())
                _assignments.Remove(id);
            SaveOrder(section.AgentId, order);
        }, ct);

    /// <inheritdoc />
    public Task AssignConversationAsync(SectionId sectionId, ConversationId conversationId, CancellationToken ct = default)
        => Locked(() =>
        {
            if (!_sections.ContainsKey(sectionId))
                throw new InvalidOperationException($"Section '{sectionId}' does not exist.");
            _assignments[conversationId.Value] = sectionId.Value;
        }, ct);

    /// <inheritdoc />
    public Task RemoveConversationAsync(ConversationId conversationId, CancellationToken ct = default)
        => Locked(() => { _assignments.Remove(conversationId.Value); }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> GetAssignmentsAsync(AgentId agentId, CancellationToken ct = default)
        => Locked<IReadOnlyDictionary<string, string>>(() =>
        {
            var ids = Owned(agentId).Select(s => s.SectionId.Value).ToHashSet(StringComparer.Ordinal);
            return _assignments.Where(a => ids.Contains(a.Value)).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        }, ct);

    private ConversationSection[] Owned(AgentId agentId) => _sections.Values.Where(s => s.AgentId == agentId)
        .OrderBy(s => s.Order).ThenBy(s => s.CreatedAt).ThenBy(s => s.SectionId.Value, StringComparer.Ordinal).ToArray();

    private IReadOnlyList<ConversationSectionOrderEntry> CurrentOrder(AgentId agentId)
        => ConversationSectionOrderEntry.Normalize(_groupOrders.GetValueOrDefault(agentId) ?? [], Owned(agentId).Select(s => s.SectionId).ToArray());

    private void SaveOrder(AgentId agentId, IReadOnlyList<ConversationSectionOrderEntry> order)
    {
        _groupOrders[agentId] = order.ToArray();
        var index = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in order)
        {
            if (entry.SectionId is { } id)
                _sections[id] = _sections[id] with { Order = index++, UpdatedAt = now };
        }
    }

    private Task<T> Locked<T>(Func<T> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(action());
    }

    private Task Locked(Action action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            action();
        return Task.CompletedTask;
    }
}
