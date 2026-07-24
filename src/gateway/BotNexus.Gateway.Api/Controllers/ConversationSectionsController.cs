using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for user-defined conversation sections (issue #2124) - personal, ordered, collapsible
/// sidebar groupings and the assignment of conversations into them. All state is persisted
/// server-side per agent/world via <see cref="IConversationSectionStore"/>, never in browser local
/// storage, so a user's organisation survives across browsers, devices, and gateway restarts.
/// </summary>
/// <remarks>
/// <b>Authorization / ownership.</b> Sections are scoped to an agent's sidebar within a world. The
/// portal transport is currently unauthenticated (see issue #527), so the effective boundary is the
/// agent + world the store stamps; when per-principal auth lands, these endpoints should additionally
/// scope to the caller's principal. Mutating endpoints resolve the owning agent from the section row,
/// so a section id is sufficient to authorize a rename/reorder/delete within the current model.
/// </remarks>
[ApiController]
[Route("api/agents/{agentId}/sections")]
public sealed class ConversationSectionsController : ControllerBase
{
    private readonly IConversationSectionStore _sections;

    /// <summary>Initialises a new instance of the <see cref="ConversationSectionsController"/> class.</summary>
    /// <param name="sections">The user-defined section store.</param>
    public ConversationSectionsController(IConversationSectionStore sections) => _sections = sections;

    /// <summary>Lists the agent's user-defined sections in display order, with conversation assignments.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(SectionListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult> List(string agentId, CancellationToken ct)
    {
        var agent = AgentId.From(agentId);
        var sections = await _sections.ListSectionsAsync(agent, ct);
        var assignments = await _sections.GetAssignmentsAsync(agent, ct);
        return Ok(new SectionListResponse(
            sections.Select(ToDto).ToList(),
            assignments.ToDictionary(kv => kv.Key, kv => kv.Value)));
    }

    /// <summary>Creates a new section for the agent.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(SectionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> Create(string agentId, [FromBody] CreateSectionRequest request, CancellationToken ct)
    {
        if (ValidateName(request.Name) is { } error)
            return BadRequest(new { error });

        var section = new ConversationSection
        {
            SectionId = SectionId.Create(),
            AgentId = AgentId.From(agentId),
            Name = request.Name.Trim(),
            IsCollapsed = request.IsCollapsed ?? false
        };
        var created = await _sections.CreateSectionAsync(section, ct);
        return CreatedAtAction(nameof(List), new { agentId }, ToDto(created));
    }

    /// <summary>Renames a section and/or updates its collapsed preference.</summary>
    [HttpPatch("{sectionId}")]
    [ProducesResponseType(typeof(SectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Update(string agentId, string sectionId, [FromBody] UpdateSectionRequest request, CancellationToken ct)
    {
        if (request.Name is null && request.IsCollapsed is null)
            return BadRequest(new { error = "name or isCollapsed is required." });
        if (request.Name is not null && ValidateName(request.Name) is { } error)
            return BadRequest(new { error });

        var updated = await _sections.UpdateSectionAsync(
            SectionId.From(sectionId),
            request.Name?.Trim(),
            request.IsCollapsed,
            ct);
        return updated is null ? NotFound() : Ok(ToDto(updated));
    }

    /// <summary>Reorders the agent's sections to match the supplied id sequence.</summary>
    [HttpPut("order")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Reorder(string agentId, [FromBody] ReorderSectionsRequest request, CancellationToken ct)
    {
        var ids = (request.SectionIds ?? []).Select(SectionId.From).ToList();
        await _sections.ReorderSectionsAsync(AgentId.From(agentId), ids, ct);
        return NoContent();
    }

    /// <summary>
    /// Deletes a section. Its conversations are returned to their system section - they are never
    /// deleted or archived.
    /// </summary>
    [HttpDelete("{sectionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Delete(string agentId, string sectionId, CancellationToken ct)
    {
        await _sections.DeleteSectionAsync(SectionId.From(sectionId), ct);
        return NoContent();
    }

    /// <summary>Assigns a conversation to the section (replacing any prior assignment).</summary>
    [HttpPut("{sectionId}/conversations/{conversationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Assign(string agentId, string sectionId, string conversationId, CancellationToken ct)
    {
        try
        {
            await _sections.AssignConversationAsync(SectionId.From(sectionId), ConversationId.From(conversationId), ct);
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { error = $"Section '{sectionId}' does not exist." });
        }
    }

    /// <summary>Removes a conversation from its section, returning it to its system section.</summary>
    [HttpDelete("conversations/{conversationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Unassign(string agentId, string conversationId, CancellationToken ct)
    {
        await _sections.RemoveConversationAsync(ConversationId.From(conversationId), ct);
        return NoContent();
    }

    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Section name is required.";
        if (name.Trim().Length > 100)
            return "Section name must be 100 characters or fewer.";
        return null;
    }

    private static SectionDto ToDto(ConversationSection s) =>
        new(s.SectionId.Value, s.AgentId.Value, s.Name, s.Order, s.IsCollapsed, s.CreatedAt, s.UpdatedAt);
}

/// <summary>Sections plus the conversation-id to section-id assignment map for the agent.</summary>
/// <param name="Sections">The agent's user-defined sections in display order.</param>
/// <param name="Assignments">Map of conversation id to the section id it is assigned to.</param>
public sealed record SectionListResponse(IReadOnlyList<SectionDto> Sections, IReadOnlyDictionary<string, string> Assignments);

/// <summary>Wire representation of a user-defined conversation section.</summary>
public sealed record SectionDto(
    string SectionId,
    string AgentId,
    string Name,
    int Order,
    bool IsCollapsed,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Request body to create a section.</summary>
public sealed record CreateSectionRequest(string Name, bool? IsCollapsed = null);

/// <summary>Request body to rename a section and/or set its collapsed preference.</summary>
public sealed record UpdateSectionRequest(string? Name = null, bool? IsCollapsed = null);

/// <summary>Request body to reorder an agent's sections.</summary>
public sealed record ReorderSectionsRequest(IReadOnlyList<string>? SectionIds);
