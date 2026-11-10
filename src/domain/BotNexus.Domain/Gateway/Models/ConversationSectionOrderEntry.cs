using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A disjoint sidebar identity: either an immutable system group or an existing custom section.
/// Names and id prefixes never determine which case an entry represents.
/// </summary>
public sealed record ConversationSectionOrderEntry
{
    private ConversationSectionOrderEntry(BuiltInConversationSection? system, SectionId? sectionId)
    {
        System = system;
        SectionId = sectionId;
    }

    /// <summary>Gets the built-in identity, or null for a custom section.</summary>
    public BuiltInConversationSection? System { get; }

    /// <summary>Gets the typed custom identity, or null for a built-in group.</summary>
    public SectionId? SectionId { get; }

    /// <summary>Creates a system identity, rejecting undefined enum values.</summary>
    public static ConversationSectionOrderEntry ForSystem(BuiltInConversationSection system)
    {
        if (!Enum.IsDefined(system))
            throw new ArgumentException("Unknown built-in conversation section.", nameof(system));
        return new(system, null);
    }

    /// <summary>Creates a custom identity; an uninitialized SectionId is not a valid reference.</summary>
    public static ConversationSectionOrderEntry ForSection(SectionId sectionId)
    {
        if (!sectionId.IsInitialized())
            throw new ArgumentException("A custom section id is required.", nameof(sectionId));
        return new(null, sectionId);
    }

    /// <summary>
    /// Repairs persisted metadata against current owned sections without writing. Missing built-ins
    /// and customs append in canonical relative order; stale references and duplicates are pruned.
    /// </summary>
    public static IReadOnlyList<ConversationSectionOrderEntry> Normalize(
        IEnumerable<ConversationSectionOrderEntry> persisted, IReadOnlyList<SectionId> ownedSectionIds)
    {
        var canonical = Enum.GetValues<BuiltInConversationSection>().Select(ForSystem)
            .Concat(ownedSectionIds.Select(ForSection)).ToArray();
        var valid = canonical.ToHashSet();
        var placed = new HashSet<ConversationSectionOrderEntry>();
        return persisted.Concat(canonical).Where(e => valid.Contains(e) && placed.Add(e)).ToArray();
    }

    /// <summary>
    /// Validates the entire submitted order before any mutation. Omitted current groups append in
    /// their prior relative order, so stale clients cannot erase newly created sections.
    /// </summary>
    public static IReadOnlyList<ConversationSectionOrderEntry> Reorder(
        IReadOnlyList<ConversationSectionOrderEntry> current, IReadOnlyList<ConversationSectionOrderEntry> requested)
    {
        var valid = current.ToHashSet();
        var placed = new HashSet<ConversationSectionOrderEntry>();
        foreach (var entry in requested)
        {
            if (entry is null || !valid.Contains(entry) || !placed.Add(entry))
                throw new ArgumentException("Group order contains a duplicate, unknown or non-owned identity.", nameof(requested));
        }
        return requested.Concat(current.Where(e => !placed.Contains(e))).ToArray();
    }

    /// <summary>
    /// Applies legacy custom-only reordering to custom slots, leaving every system slot unchanged.
    /// Duplicate and foreign ids are ignored and omitted customs retain their prior relative order.
    /// </summary>
    public static IReadOnlyList<ConversationSectionOrderEntry> ReorderCustomSlots(
        IReadOnlyList<ConversationSectionOrderEntry> current, IReadOnlyList<SectionId> requested)
    {
        var customs = current.Where(e => e.SectionId.HasValue).ToArray();
        var owned = customs.Select(e => e.SectionId.GetValueOrDefault()).ToHashSet();
        var placed = new HashSet<SectionId>();
        var reordered = requested.Where(id => owned.Contains(id) && placed.Add(id)).Select(ForSection)
            .Concat(customs.Where(e => !placed.Contains(e.SectionId.GetValueOrDefault()))).ToArray();
        var index = 0;
        return current.Select(e => e.SectionId.HasValue ? reordered[index++] : e).ToArray();
    }
}
