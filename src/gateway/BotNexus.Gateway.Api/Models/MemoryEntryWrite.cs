using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Api.Models;

/// <summary>
/// A memory note as the portal sends it — what the operator typed, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no provenance field here, and there must never be one.</b> Trust in
/// this subsystem is derived from <c>MemoryProvenance</c> (#3232), and a write arriving on this
/// route is by definition a first-party human instruction from the agent's owner, so the controller
/// stamps <c>MemoryProvenance.User</c> itself. Letting a caller name its own provenance would turn
/// this endpoint into a laundering path: hostile third-party text posted as <c>user</c> would be
/// promoted from <c>Quarantined</c> to <c>Trusted</c> — canon-eligible, injectable into always-on
/// context, and promotable into a shared store — which is precisely what the trust model exists to
/// prevent.
/// </para>
/// <para>
/// Stamping first-party is not the same as trusting the text blindly. Trust is derived from
/// provenance <i>and</i> content, so quarantine markers in the body still downgrade the row on
/// every read, and the content is run through the same sanitizer the transcript indexer uses before
/// it is persisted.
/// </para>
/// </remarks>
public sealed record MemoryEntryWrite
{
    /// <summary>The note itself. Required; whitespace alone is not a note.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>
    /// Optional classification — decision, pattern, fact, procedure, preference — matching what the
    /// agent-facing <c>memory_save</c> tool accepts, so notes written by hand and notes written by
    /// the agent are filterable the same way.
    /// </summary>
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    /// <summary>Optional tags for filtering.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }
}
