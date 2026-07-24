using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Domain model for a user-defined conversation section - a personal, ordered, collapsible grouping
/// the user creates in the portal sidebar to organise conversations beyond the built-in system
/// sections (Pinned, Conversations, Scheduled, Webhooks). See issue #2124.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership / authorization.</b> A section is owned by the sidebar of a single agent within a
/// single world (<see cref="AgentId"/> + <see cref="WorldId"/>). The world discriminator is stamped
/// by the store on persistence - exactly as conversations are (Phase 9 / P9-A) - so two worlds that
/// happen to share an agent id never see each other's sections. Because the portal REST surface is
/// currently unauthenticated at the transport layer, "per user" is modelled today as "per agent
/// sidebar in a world": every conversation an agent owns is visible to that agent's portal view, and
/// so are its sections. When per-principal auth lands (issue #527) the owning citizen should be
/// stamped alongside the world id and the list/mutation endpoints scoped to the caller's principal.
/// </para>
/// <para>
/// A conversation may be assigned to <b>at most one</b> user-defined section
/// (<c>IConversationSectionStore.AssignConversationAsync</c> is an upsert that replaces any prior
/// assignment). System attributes - pinning, cron/webhook provenance - are never destroyed by a
/// custom assignment; the sidebar applies presentation precedence (pinned always renders in Pinned)
/// on top of the custom grouping.
/// </para>
/// </remarks>
public sealed record ConversationSection
{
    /// <summary>Gets the stable unique identifier for this section.</summary>
    public SectionId SectionId { get; init; }

    /// <summary>Gets the agent whose sidebar owns this section.</summary>
    public AgentId AgentId { get; init; }

    /// <summary>
    /// Gets or sets the id of the world this section belongs to. Stamped by the store on
    /// persistence from the gateway's resolved world identity; defaults to empty string so
    /// pre-existing rows and in-memory constructs deserialise unchanged.
    /// </summary>
    public string WorldId { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable display name shown as the section header.</summary>
    public string Name { get; set; } = "New section";

    /// <summary>
    /// Gets or sets the zero-based ordering position of this section relative to the agent's other
    /// user-defined sections. Lower values render first. Reassigned atomically by
    /// <c>IConversationSectionStore.ReorderSectionsAsync</c>.
    /// </summary>
    public int Order { get; set; }

    /// <summary>Gets or sets whether the section is rendered collapsed in the sidebar.</summary>
    public bool IsCollapsed { get; set; }

    /// <summary>Gets or sets when this section was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets when this section was last modified (rename / reorder / collapse).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
