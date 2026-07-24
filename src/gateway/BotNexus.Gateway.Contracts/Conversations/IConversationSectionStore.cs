using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Persistence contract for user-defined conversation sections and their conversation assignments
/// (issue #2124). Sections are personal, ordered, collapsible groupings scoped to a single agent's
/// sidebar within a single world; a conversation may belong to at most one section.
/// </summary>
/// <remarks>
/// <para>
/// Assignment and ordering are stored server-side (SQLite in production, in-memory for tests/dev) so
/// a user's organisation survives across browsers, devices, and gateway restarts - never in browser
/// local storage. Deleting a section returns its conversations to their system section by removing
/// the assignment rows only; the conversations themselves are never deleted or archived.
/// </para>
/// <para>All implementations must be thread-safe.</para>
/// </remarks>
public interface IConversationSectionStore
{
    /// <summary>
    /// Lists the agent's user-defined sections in ascending <see cref="ConversationSection.Order"/>.
    /// Returns an empty list when the agent has no sections.
    /// </summary>
    Task<IReadOnlyList<ConversationSection>> ListSectionsAsync(AgentId agentId, CancellationToken ct = default);

    /// <summary>Gets a single section by id, or <c>null</c> when it does not exist.</summary>
    Task<ConversationSection?> GetSectionAsync(SectionId sectionId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new section for the agent. The <see cref="ConversationSection.Order"/> is assigned as
    /// the next position after the agent's existing sections. Returns the persisted section.
    /// </summary>
    Task<ConversationSection> CreateSectionAsync(ConversationSection section, CancellationToken ct = default);

    /// <summary>
    /// Renames a section and/or updates its collapsed preference. A <c>null</c> argument leaves that
    /// field unchanged. No-op when the section does not exist; returns the updated section or
    /// <c>null</c> when it was not found.
    /// </summary>
    Task<ConversationSection?> UpdateSectionAsync(SectionId sectionId, string? name, bool? isCollapsed, CancellationToken ct = default);

    /// <summary>
    /// Reorders the agent's sections to match the supplied id sequence. Ids not owned by the agent are
    /// ignored; any of the agent's sections omitted from <paramref name="orderedSectionIds"/> keep
    /// their relative order after the supplied ones. Idempotent.
    /// </summary>
    Task ReorderSectionsAsync(AgentId agentId, IReadOnlyList<SectionId> orderedSectionIds, CancellationToken ct = default);

    /// <summary>
    /// Deletes a section and all its conversation assignments. The conversations are NOT deleted or
    /// archived - removing the assignment rows returns them to their system section. No-op when the
    /// section does not exist.
    /// </summary>
    Task DeleteSectionAsync(SectionId sectionId, CancellationToken ct = default);

    /// <summary>
    /// Assigns a conversation to a section (upsert): any prior section assignment for the conversation
    /// is replaced, enforcing the at-most-one-section invariant. Throws
    /// <see cref="InvalidOperationException"/> when the target section does not exist.
    /// </summary>
    Task AssignConversationAsync(SectionId sectionId, ConversationId conversationId, CancellationToken ct = default);

    /// <summary>
    /// Removes a conversation from whatever section it is assigned to, returning it to its system
    /// section. No-op when the conversation has no assignment.
    /// </summary>
    Task RemoveConversationAsync(ConversationId conversationId, CancellationToken ct = default);

    /// <summary>
    /// Returns the map of conversation id to section id for every assignment owned by the agent, so the
    /// sidebar can render each conversation under its custom section in one round-trip.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetAssignmentsAsync(AgentId agentId, CancellationToken ct = default);
}
