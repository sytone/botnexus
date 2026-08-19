using BotNexus.Gateway.Api.Controllers;

namespace BotNexus.Gateway.Api.Export;

/// <summary>
/// Scope discriminator for an <see cref="ExportDocument"/> - whether it was assembled for a whole
/// conversation or for a single session.
/// </summary>
public enum ExportScope
{
    /// <summary>The document covers every session linked to one conversation.</summary>
    Conversation,

    /// <summary>The document covers exactly one session.</summary>
    Session,

    /// <summary>
    /// The document covers a contiguous partial range of a conversation or session (issue #3279).
    /// </summary>
    /// <remarks>
    /// Reported whenever a range selector was supplied, including when that range happens to span
    /// the whole transcript. A full-span excerpt is deliberately NOT reported back as
    /// <see cref="Conversation"/>: the caller asked for a range, the endpoints are recorded in the
    /// document, and a reader must be able to tell "this is everything" from "this is everything
    /// that existed at the moment the range was pinned".
    /// </remarks>
    Excerpt
}

/// <summary>
/// Per-session metadata carried in an <see cref="ExportDocument"/>'s summary header (issue #3278,
/// acceptance criterion 1).
/// </summary>
/// <param name="SessionId">The session identifier.</param>
/// <param name="AgentId">The agent that owns the session.</param>
/// <param name="CreatedAt">When the session was started.</param>
/// <param name="UpdatedAt">When the session was last written to.</param>
/// <param name="Status">The session lifecycle status (e.g. <c>Active</c>, <c>Sealed</c>).</param>
/// <param name="MessageCount">Number of exported message entries attributed to this session.</param>
public sealed record ExportSessionInfo(
    string SessionId,
    string? AgentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Status,
    int MessageCount);

/// <summary>
/// The single, render-agnostic export document model that both the conversation and session export
/// scopes populate (issue #3278, acceptance criterion 1).
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately carries no formatting: it is the assembled facts (summary header plus the
/// chronological entries) and every renderer is a pure projection over it. That is what makes
/// "Markdown and HTML agree" a structural property rather than a pair of hand-maintained
/// implementations that drift.
/// </para>
/// <para>
/// The <see cref="Entries"/> list is produced by <see cref="ConversationHistoryProjection"/> - the
/// same projection the portal history endpoint consumes - so an export can never disagree with what
/// the portal shows for the same conversation. See the anti-drift clause of #3278.
/// </para>
/// </remarks>
public sealed record ExportDocument
{
    /// <summary>Gets whether this document covers a conversation or a single session.</summary>
    public required ExportScope Scope { get; init; }

    /// <summary>
    /// Gets the conversation identifier. Populated for conversation scope, and for session scope
    /// when the session is linked to a conversation (acceptance criterion 4); <c>null</c> for an
    /// orphan session.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>Gets the conversation title, when a conversation summary is available.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the conversation purpose, when set.</summary>
    public string? Purpose { get; init; }

    /// <summary>Gets the conversation lifecycle status (<c>Active</c> / <c>Archived</c>), when available.</summary>
    public string? Status { get; init; }

    /// <summary>Gets when the conversation was created, when available.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Gets when the conversation was last updated, when available.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Gets the agent that owns the exported conversation or session.</summary>
    public string? AgentId { get; init; }

    /// <summary>Gets the conversation-scoped instructions injected into the system prompt, when set.</summary>
    public string? Instructions { get; init; }

    /// <summary>Gets the per-conversation model override, when set.</summary>
    public string? ModelOverride { get; init; }

    /// <summary>Gets the per-conversation thinking-level override token, when set.</summary>
    public string? ThinkingOverride { get; init; }

    /// <summary>Gets the per-conversation context-window override in tokens, when set.</summary>
    public int? ContextWindowOverride { get; init; }

    /// <summary>Gets the sessions included in this document, in chronological order.</summary>
    public IReadOnlyList<ExportSessionInfo> Sessions { get; init; } = [];

    /// <summary>
    /// Gets the chronological transcript entries, exactly as produced by
    /// <see cref="ConversationHistoryProjection.Project"/>.
    /// </summary>
    public IReadOnlyList<ConversationHistoryEntry> Entries { get; init; } = [];

    /// <summary>
    /// Gets the number of exported message entries (kind <c>message</c>). Boundary and compaction
    /// markers are structural, not messages, so they are excluded.
    /// </summary>
    public int MessageCount { get; init; }

    /// <summary>Gets the number of exported tool-call entries.</summary>
    public int ToolCallCount { get; init; }

    /// <summary>Gets the instant the export was generated, used for the download filename.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Gets the range selector this document was produced from, or <see langword="null"/> for a full
    /// export (issue #3279).
    /// </summary>
    public ExportRangeSelector? Range { get; init; }

    /// <summary>
    /// Gets the number of transcript entries that exist outside the selected range and are therefore
    /// absent from this document (issue #3279, acceptance criterion 3). Zero for a full export and
    /// for a range that happens to span the whole transcript.
    /// </summary>
    public int OmittedEntryCount { get; init; }

    /// <summary>
    /// Gets the explicit "content was omitted" note every ranged export carries, or
    /// <see langword="null"/> for a full export (issue #3279, acceptance criterion 3).
    /// </summary>
    /// <remarks>
    /// Held on the document rather than composed inside each renderer so Markdown and HTML cannot
    /// disagree about whether - or how loudly - a partial transcript declares itself partial. A
    /// full-span range still carries the note: the reader is being told the document was produced
    /// from a selection, which remains true and remains material even when nothing was dropped.
    /// </remarks>
    public string? OmissionNote { get; init; }
}
