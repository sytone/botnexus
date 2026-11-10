using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Domain model for a user-defined conversation section - a personal, ordered, collapsible grouping
/// the user creates in the portal sidebar to organise conversations beyond the built-in system
/// sections (Pinned, Conversations, Scheduled, Webhooks). See issue #2124.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership / authorization.</b> A custom section belongs to one agent's sidebar. The store
/// stamps WorldId, but inherited reads are agent/data-root scoped, not WorldId-filtered. This model
/// does not establish cross-world or per-principal authorization; routes must check target AgentId.
/// </para>
/// <para>
/// A conversation may be assigned to <b>at most one</b> user-defined section
/// (<c>IConversationSectionStore.AssignConversationAsync</c> is an upsert that replaces any prior
/// assignment). System attributes - pinning, cron/webhook provenance - are never destroyed by a
/// custom assignment. A valid explicit custom assignment wins presentation placement; removing it
/// restores the current natural home (Pinned, Scheduled, Webhooks or Conversations).
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
    /// user-defined sections. This is the custom subsequence of the separate unified peer-group order,
    /// synchronized atomically by either reorder API.
    /// </summary>
    public int Order { get; set; }

    /// <summary>Gets or sets whether the section is rendered collapsed in the sidebar.</summary>
    public bool IsCollapsed { get; set; }

    /// <summary>Gets when this section was created. Write-once creation-time fact.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets when this section was last modified (rename / reorder / collapse).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
