using BotNexus.Domain.Primitives;
using BotNexus.Domain.Text;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Memory;
using BotNexus.Memory.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST endpoints for inspecting per-agent memory store statistics.
/// </summary>
[ApiController]
[Route("api/memory")]
public sealed class MemoryController(
    IAgentRegistry agentRegistry,
    IMemoryStoreFactory memoryStoreFactory,
    ILogger<MemoryController> logger,
    ISharedMemoryStoreRegistry? sharedStores = null) : ControllerBase
{
    /// <summary>
    /// Lists all agents that have memory enabled along with their store statistics.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListMemoryStores(CancellationToken ct)
    {
        var agents = agentRegistry.GetAll()
            .Where(a => a.Memory is { Enabled: true })
            .OrderBy(a => a.AgentId.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<MemoryStoreDto>(agents.Count);
        foreach (var agent in agents)
        {
            var dto = await GetStatsForAgentAsync(agent.AgentId.Value, ct).ConfigureAwait(false);
            if (dto is not null)
                results.Add(dto);
        }

        return Ok(results);
    }

    /// <summary>
    /// Lists the shared memory stores and who may read or write each one.
    /// </summary>
    /// <remarks>
    /// Access lists rather than contents. Who can reach a shared store is the thing an operator
    /// has to be able to check, and it is the thing that is invisible everywhere else - a store
    /// several agents write to is a channel one agent can use to influence the others, and the
    /// only place that relationship is written down is config.json.
    /// <para>
    /// The registry parameter is optional to match every other consumer of it, so this endpoint
    /// answers with an empty list rather than a 500 on a gateway that has none.
    /// </para>
    /// </remarks>
    [HttpGet("shared")]
    public IActionResult ListSharedStores()
    {
        if (sharedStores is null)
            return Ok(Array.Empty<SharedMemoryStoreDto>());

        var agentIds = agentRegistry.GetAll()
            .Select(a => a.AgentId.Value)
            .ToList();

        var dtos = sharedStores.GetAllConfigs()
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new SharedMemoryStoreDto
            {
                Name = c.Name,
                Description = c.Description,
                Readers = c.Readers,
                Writers = c.Writers,
                RetentionDays = c.RetentionDays,
                // Resolved against the live roster so "*" reads as a number rather than a glyph
                // the operator has to expand in their head.
                ReaderCount = agentIds.Count(id => sharedStores.CanRead(id, c.Name)),
                WriterCount = agentIds.Count(id => sharedStores.CanWrite(id, c.Name))
            })
            .ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Gets memory store statistics for a specific agent.
    /// </summary>
    [HttpGet("{agentId}")]
    public async Task<IActionResult> GetMemoryStore(string agentId, CancellationToken ct)
    {
        var descriptor = agentRegistry.Get(AgentId.From(agentId));
        if (descriptor is null)
            return NotFound(new { error = $"Agent '{agentId}' not found." });

        if (descriptor.Memory is not { Enabled: true })
            return NotFound(new { error = $"Agent '{agentId}' does not have memory enabled." });

        var dto = await GetStatsForAgentAsync(agentId, ct).ConfigureAwait(false);
        return dto is not null ? Ok(dto) : NotFound(new { error = $"Memory store for agent '{agentId}' is not available." });
    }

    /// <summary>
    /// Searches memory entries for a specific agent. Requires a query parameter.
    /// </summary>
    [HttpGet("{agentId}/entries")]
    public async Task<IActionResult> SearchEntries(
        string agentId,
        [FromQuery] string? query = null,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var descriptor = agentRegistry.Get(AgentId.From(agentId));
        if (descriptor is null)
            return NotFound(new { error = $"Agent '{agentId}' not found." });

        if (descriptor.Memory is not { Enabled: true })
            return NotFound(new { error = $"Agent '{agentId}' does not have memory enabled." });

        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { error = "Query parameter is required for entry search." });

        limit = Math.Clamp(limit, 1, 100);

        try
        {
            var store = memoryStoreFactory.Create(AgentId.From(agentId));
            await store.InitializeAsync(ct).ConfigureAwait(false);

            var result = await store.SearchWithReportAsync(query, limit, ct: ct).ConfigureAwait(false);

            var dtos = result.Entries.Select(scored => ToDto(scored.Entry)).ToList();

            // #3244: the scan report travels with the results, so "no older match" and "older rows
            // were never scored" are distinguishable in the UI instead of looking identical.
            return Ok(new
            {
                agentId,
                query,
                entries = dtos,
                count = dtos.Count,
                vectorScan = new
                {
                    status = result.VectorScan.Status.ToString(),
                    possiblyTruncated = result.VectorScan.IsPossiblyTruncated,
                    rowsScanned = result.VectorScan.RowsScanned,
                    scanCeiling = result.VectorScan.ScanCeiling,
                    lexicalUnionRowsScanned = result.VectorScan.LexicalUnionRowsScanned,
                    explanation = result.VectorScan.Explain()
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to search memory entries for agent '{AgentId}'.", agentId);
            return StatusCode(500, new { error = "Failed to access memory store." });
        }
    }

    /// <summary>
    /// Lists an agent's most recent memory entries, newest first.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate route rather than relaxing <see cref="SearchEntries"/> to list when
    /// its query is blank: that endpoint's "query is required" 400 is pinned by a test, and search
    /// answers a different question anyway. A query returns what matches; this returns what is
    /// there, which is what an operator managing memory by hand needs to see.
    /// </remarks>
    [HttpGet("{agentId}/entries/recent")]
    public async Task<IActionResult> ListRecentEntries(
        string agentId, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        if (Reject(agentId) is { } guard)
            return guard;

        try
        {
            var store = memoryStoreFactory.Create(AgentId.From(agentId));
            await store.InitializeAsync(ct).ConfigureAwait(false);

            var entries = await store.ListRecentAsync(Math.Clamp(limit, 1, 200), ct).ConfigureAwait(false);
            return Ok(new { agentId, entries = entries.Select(ToDetailDto).ToList(), count = entries.Count });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list memory entries for agent '{AgentId}'.", agentId);
            return StatusCode(500, new { error = "Failed to access memory store." });
        }
    }

    /// <summary>
    /// Adds a memory note written by the operator.
    /// </summary>
    /// <remarks>
    /// Stamps <see cref="MemoryProvenance.User"/> — a write on this route is a first-party human
    /// instruction from the agent's owner — and never reads a provenance from the body. See
    /// <see cref="MemoryEntryWrite"/> for why that asymmetry is the whole security property.
    /// The content still passes through <see cref="UntrustedContentSanitizer"/> first: stamping the
    /// row first-party is a statement about who asked for it, not a promise about what it contains.
    /// </remarks>
    [HttpPost("{agentId}/entries")]
    public async Task<IActionResult> AddEntry(string agentId, [FromBody] MemoryEntryWrite request, CancellationToken ct)
    {
        if (Reject(agentId) is { } guard)
            return guard;

        var content = UntrustedContentSanitizer.Sanitize(request?.Content);
        if (string.IsNullOrWhiteSpace(content))
            return BadRequest(new { error = "Content is required." });

        if (content.Length > MaxEntryLength)
            return BadRequest(new { error = $"Content exceeds the {MaxEntryLength} character limit." });

        try
        {
            var store = memoryStoreFactory.Create(AgentId.From(agentId));
            await store.InitializeAsync(ct).ConfigureAwait(false);

            var entry = new MemoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                AgentId = agentId,
                SourceType = ManualSourceType,
                Content = content,
                CreatedAt = DateTimeOffset.UtcNow,
                Provenance = MemoryProvenance.User,
                MetadataJson = BuildMetadataJson(request?.Category, request?.Tags)
            };

            var saved = await store.InsertAsync(entry, ct).ConfigureAwait(false);
            return Ok(ToDetailDto(saved));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to add a memory entry for agent '{AgentId}'.", agentId);
            return StatusCode(500, new { error = "Failed to write to the memory store." });
        }
    }

    /// <summary>
    /// Replaces the content of one memory note, keeping its id and original creation time.
    /// </summary>
    /// <remarks>
    /// <see cref="IMemoryStore"/> has no update verb, so this deletes and re-inserts under the SAME
    /// id rather than adding one. That reuses two paths this store already tests, and the FTS index
    /// follows automatically through the insert/delete triggers — an <c>UpdateAsync</c> bolted on
    /// here would have to re-derive the embedding and re-sync FTS by hand, which is exactly the kind
    /// of second, subtly different write path that drifts.
    /// <para>
    /// It is not atomic: a crash between the delete and the insert loses the note. The window is
    /// small and the failure is visible rather than silent, which is the right trade against
    /// maintaining a parallel write path — but it is a real limitation, not an oversight.
    /// </para>
    /// </remarks>
    [HttpPut("{agentId}/entries/{entryId}")]
    public async Task<IActionResult> UpdateEntry(
        string agentId, string entryId, [FromBody] MemoryEntryWrite request, CancellationToken ct)
    {
        if (Reject(agentId) is { } guard)
            return guard;

        var content = UntrustedContentSanitizer.Sanitize(request?.Content);
        if (string.IsNullOrWhiteSpace(content))
            return BadRequest(new { error = "Content is required." });

        if (content.Length > MaxEntryLength)
            return BadRequest(new { error = $"Content exceeds the {MaxEntryLength} character limit." });

        try
        {
            var store = memoryStoreFactory.Create(AgentId.From(agentId));
            await store.InitializeAsync(ct).ConfigureAwait(false);

            var existing = await store.GetByIdAsync(entryId, ct).ConfigureAwait(false);
            if (existing is null)
                return NotFound(new { error = $"Memory entry '{entryId}' not found." });

            // Re-stamped as a first-party human write, because that is what just happened to it -
            // and the embedding is dropped so InsertAsync regenerates it from the new content
            // rather than leaving a vector that describes the text this note used to hold.
            var replacement = existing with
            {
                Content = content,
                Provenance = MemoryProvenance.User,
                SourceType = ManualSourceType,
                MetadataJson = BuildMetadataJson(request?.Category, request?.Tags) ?? existing.MetadataJson,
                Embedding = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await store.DeleteAsync(entryId, ct).ConfigureAwait(false);
            var saved = await store.InsertAsync(replacement, ct).ConfigureAwait(false);
            return Ok(ToDetailDto(saved));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update memory entry '{EntryId}' for agent '{AgentId}'.", entryId, agentId);
            return StatusCode(500, new { error = "Failed to write to the memory store." });
        }
    }

    /// <summary>Deletes one memory note.</summary>
    [HttpDelete("{agentId}/entries/{entryId}")]
    public async Task<IActionResult> DeleteEntry(string agentId, string entryId, CancellationToken ct)
    {
        if (Reject(agentId) is { } guard)
            return guard;

        try
        {
            var store = memoryStoreFactory.Create(AgentId.From(agentId));
            await store.InitializeAsync(ct).ConfigureAwait(false);

            var existing = await store.GetByIdAsync(entryId, ct).ConfigureAwait(false);
            if (existing is null)
                return NotFound(new { error = $"Memory entry '{entryId}' not found." });

            await store.DeleteAsync(entryId, ct).ConfigureAwait(false);
            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete memory entry '{EntryId}' for agent '{AgentId}'.", entryId, agentId);
            return StatusCode(500, new { error = "Failed to write to the memory store." });
        }
    }

    /// <summary>Notes written by hand carry their own source type, so they are filterable apart
    /// from indexed conversation turns and from what the agent saved itself.</summary>
    private const string ManualSourceType = "manual";

    /// <summary>A note, not a document. Long enough for a paragraph of business background.</summary>
    private const int MaxEntryLength = 8000;

    /// <summary>
    /// The guard the three write verbs share with the reads above: the agent must exist and have
    /// memory enabled. Returns null when the request may proceed.
    /// </summary>
    private IActionResult? Reject(string agentId)
    {
        var descriptor = agentRegistry.Get(AgentId.From(agentId));
        if (descriptor is null)
            return NotFound(new { error = $"Agent '{agentId}' not found." });

        return descriptor.Memory is not { Enabled: true }
            ? NotFound(new { error = $"Agent '{agentId}' does not have memory enabled." })
            : null;
    }

    private static MemoryEntryDto ToDto(MemoryEntry entry) => new(
        Id: entry.Id,
        CreatedAt: entry.CreatedAt,
        SourceType: entry.SourceType,
        SessionId: entry.SessionId,
        ContentPreview: TextTruncation.SafeTruncate(entry.Content, 200, "...")!,
        Provenance: entry.NormalizedProvenance,
        TrustTier: entry.TrustTier.ToString(),
        IsFirstParty: entry.IsFirstParty,
        UserId: entry.UserId,
        ExpiresAt: entry.ExpiresAt);

    private static MemoryEntryDetailDto ToDetailDto(MemoryEntry entry) => new(
        Id: entry.Id,
        CreatedAt: entry.CreatedAt,
        UpdatedAt: entry.UpdatedAt,
        SourceType: entry.SourceType,
        SessionId: entry.SessionId,
        Content: entry.Content,
        Provenance: entry.NormalizedProvenance,
        TrustTier: entry.TrustTier.ToString(),
        IsFirstParty: entry.IsFirstParty);

    private static string? BuildMetadataJson(string? category, IReadOnlyList<string>? tags)
    {
        var cleanTags = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (string.IsNullOrWhiteSpace(category) && (cleanTags is null || cleanTags.Count == 0))
            return null;

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(category))
            payload["category"] = category.Trim();
        if (cleanTags is { Count: > 0 })
            payload["tags"] = cleanTags;

        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    private async Task<MemoryStoreDto?> GetStatsForAgentAsync(string agentId, CancellationToken ct)
    {
        try
        {
            var store = memoryStoreFactory.Create(AgentId.From(agentId));
            await store.InitializeAsync(ct).ConfigureAwait(false);
            var stats = await store.GetStatsAsync(ct).ConfigureAwait(false);
            return new MemoryStoreDto(
                AgentId: agentId,
                EntryCount: stats.EntryCount,
                DatabaseSizeBytes: stats.DatabaseSizeBytes,
                LastIndexedAt: stats.LastIndexedAt,
                EmbeddedEntryCount: stats.EmbeddedEntryCount,
                VectorScanCeiling: stats.VectorScanCeiling,
                ExceedsVectorScanCeiling: stats.ExceedsVectorScanCeiling);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get memory stats for agent '{AgentId}'.", agentId);
            return null;
        }
    }
}

/// <summary>
/// Per-agent memory store row for the Memory tab.
/// </summary>
/// <remarks>
/// The vector-scan trio (#3244) is rendered so an operator can see silent recall truncation as a
/// store property. <c>EmbeddedEntryCount</c> alone would be meaningless without the ceiling it is
/// compared against, and <c>ExceedsVectorScanCeiling</c> is projected server-side so the UI cannot
/// invent a second, drifting definition of the condition.
/// </remarks>
internal sealed record MemoryStoreDto(
    string AgentId,
    int EntryCount,
    long DatabaseSizeBytes,
    DateTimeOffset? LastIndexedAt,
    int EmbeddedEntryCount,
    int? VectorScanCeiling,
    bool ExceedsVectorScanCeiling);

/// <remarks>
/// Carries the same trust trio as <see cref="MemoryEntryDetailDto"/>, for the reason that record
/// already gives: someone pruning memory needs to see which notes are quarantined or untrusted,
/// because that is usually why they are looking. The split between the two stays exactly where it
/// was - this one truncates to a preview and must never be loaded into an editor - so widening it
/// with trust does not blur the distinction, which is about CONTENT rather than about metadata.
/// <para>
/// <c>ExpiresAt</c> travels too: an entry past its expiry is invisible to search but still
/// deletable by id, and an operator looking at a list has no other way to tell.
/// </para>
/// </remarks>
internal sealed record MemoryEntryDto(
    string Id,
    DateTimeOffset CreatedAt,
    string SourceType,
    string? SessionId,
    string ContentPreview,
    string Provenance,
    string TrustTier,
    bool IsFirstParty,
    string? UserId,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// One entry as the management surface needs it, carrying the WHOLE note.
/// </summary>
/// <remarks>
/// Separate from <see cref="MemoryEntryDto"/> on purpose. That projection truncates to a 200-char
/// preview, which is right for search results and catastrophic for an editor: loading a preview
/// into an edit box and saving it back silently destroys everything past the truncation point. A
/// caller that can edit must receive the full text, so the two projections stay distinct rather
/// than one growing a flag.
/// <para>
/// The trust fields travel with it because an operator pruning memory needs to see which notes are
/// quarantined or untrusted - that is usually the reason they are looking.
/// </para>
/// </remarks>
internal sealed record MemoryEntryDetailDto(
    string Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string SourceType,
    string? SessionId,
    string Content,
    string Provenance,
    string TrustTier,
    bool IsFirstParty);
