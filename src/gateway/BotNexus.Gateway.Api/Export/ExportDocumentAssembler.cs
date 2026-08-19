using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;

namespace BotNexus.Gateway.Api.Export;

/// <summary>
/// Builds an <see cref="ExportDocument"/> for a conversation or a single session (issue #3278).
/// </summary>
/// <remarks>
/// Both scopes funnel through <see cref="ConversationHistoryProjection.Project"/>, which is the same
/// projection <see cref="ConversationHistoryAssembler"/> serves the portal history endpoint from.
/// This is the anti-drift guarantee of #3278 acceptance criterion 9: there is no second transcript
/// interpretation, only a second consumer of the first one.
/// </remarks>
public sealed class ExportDocumentAssembler
{
    private readonly IConversationStore _conversations;
    private readonly ISessionStore _sessions;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportDocumentAssembler"/> class.
    /// </summary>
    /// <param name="conversations">The conversation store.</param>
    /// <param name="sessions">The session store.</param>
    /// <param name="timeProvider">Clock used to stamp <see cref="ExportDocument.GeneratedAt"/>; defaults to the system clock.</param>
    public ExportDocumentAssembler(
        IConversationStore conversations,
        ISessionStore sessions,
        TimeProvider? timeProvider = null)
    {
        _conversations = conversations;
        _sessions = sessions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Assembles the export document for a whole conversation, covering every linked session.
    /// </summary>
    /// <param name="conversationId">The conversation to export.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The assembled document, or <c>null</c> when the conversation does not exist (callers map
    /// <c>null</c> to 404). An <em>empty</em> conversation is not null: it returns a document with a
    /// populated header and no entries, so a user exporting a conversation they just created gets a
    /// valid file describing it rather than a 404 (#3278 acceptance criterion 8).
    /// </returns>
    public async Task<ExportDocument?> AssembleConversationAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetAsync(conversationId, cancellationToken);
        if (conversation is null)
            return null;

        var linkedSessions = await _sessions.ListByConversationAsync(conversationId, cancellationToken: cancellationToken);

        // Same #732 fallback the history assembler applies: cron sessions and pre-migration rows may
        // carry a NULL conversation_id, so the indexed query returns nothing even though the
        // conversation has messages. Without this an export of such a conversation would silently be
        // an empty transcript.
        if (linkedSessions.Count == 0 && conversation.ActiveSessionId is { } fallbackSessionId)
        {
            var fallbackSession = await _sessions.GetAsync(fallbackSessionId, cancellationToken);
            if (fallbackSession is not null)
                linkedSessions = [fallbackSession];
        }

        var entries = ConversationHistoryProjection.Project(linkedSessions);

        return new ExportDocument
        {
            Scope = ExportScope.Conversation,
            ConversationId = conversation.ConversationId.Value,
            Title = conversation.Title,
            Purpose = conversation.Purpose,
            Status = conversation.Status.ToString(),
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
            AgentId = conversation.AgentId.Value,
            Instructions = conversation.Instructions,
            ModelOverride = conversation.ModelOverride,
            ThinkingOverride = conversation.ThinkingOverride,
            ContextWindowOverride = conversation.ContextWindowOverride,
            Sessions = BuildSessionInfos(linkedSessions, entries),
            Entries = entries,
            MessageCount = CountMessages(entries),
            ToolCallCount = CountToolCalls(entries),
            GeneratedAt = _timeProvider.GetUtcNow()
        };
    }

    /// <summary>
    /// Assembles the export document for a single session, including its parent conversation summary
    /// when the session is linked to one (#3278 acceptance criterion 4).
    /// </summary>
    /// <param name="sessionId">The session to export.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assembled document, or <c>null</c> when the session does not exist.</returns>
    public async Task<ExportDocument?> AssembleSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return null;

        var entries = ConversationHistoryProjection.Project([session]);

        // A session may be linked to a conversation, orphaned (#732), or point at a conversation row
        // that no longer exists. Only a successful lookup contributes the parent summary; the export
        // must still succeed in the other two cases with a session-only header.
        //
        // ConversationId is a Vogen value object, so on an orphan session it is UNINITIALIZED and
        // reading .Value throws ValueObjectValidationException rather than returning null or empty.
        // IsInitialized must therefore be checked before the value is touched at all.
        var linkedConversationId = session.ConversationId.IsInitialized()
            && !string.IsNullOrWhiteSpace(session.ConversationId.Value)
                ? session.ConversationId.Value
                : null;

        Conversation? conversation = null;
        if (linkedConversationId is not null)
            conversation = await _conversations.GetAsync(session.ConversationId, cancellationToken);

        return new ExportDocument
        {
            Scope = ExportScope.Session,
            ConversationId = conversation?.ConversationId.Value ?? linkedConversationId,
            Title = conversation?.Title,
            Purpose = conversation?.Purpose,
            Status = conversation?.Status.ToString(),
            CreatedAt = conversation?.CreatedAt,
            UpdatedAt = conversation?.UpdatedAt,
            AgentId = session.AgentId.Value,
            Instructions = conversation?.Instructions,
            ModelOverride = conversation?.ModelOverride,
            ThinkingOverride = conversation?.ThinkingOverride,
            ContextWindowOverride = conversation?.ContextWindowOverride,
            Sessions = BuildSessionInfos([session], entries),
            Entries = entries,
            MessageCount = CountMessages(entries),
            ToolCallCount = CountToolCalls(entries),
            GeneratedAt = _timeProvider.GetUtcNow()
        };
    }

    /// <summary>
    /// Assembles a partial-range export over a conversation (issue #3279, acceptance criteria 1-4).
    /// </summary>
    /// <param name="conversationId">The conversation to export.</param>
    /// <param name="range">
    /// The first/last entry selector. When <see langword="null"/> this is exactly
    /// <see cref="AssembleConversationAsync"/>, so the two paths cannot drift.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A success result carrying the excerpt, or a specific rejection. The range is never clamped:
    /// an endpoint that does not resolve produces a rejection, not a trimmed document whose summary
    /// header would then misdescribe its own contents (acceptance criterion 4).
    /// </returns>
    public async Task<ExportRangeResult> AssembleConversationRangeAsync(
        ConversationId conversationId,
        ExportRangeSelector? range,
        CancellationToken cancellationToken = default)
    {
        var full = await AssembleConversationAsync(conversationId, cancellationToken);
        if (full is null)
            return ExportRangeResult.SubjectNotFound();

        if (range is null)
            return ExportRangeResult.Success(full);

        // Sessions that exist but are not part of THIS conversation, so an endpoint naming one can
        // be reported as a cross-conversation mix-up rather than as an indistinguishable "no such
        // entry". The distinction matters to a caller who pasted the wrong link.
        var ownSessionIds = full.Sessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        var foreign = await ForeignSessionIdsAsync(range, ownSessionIds, cancellationToken);

        return ApplyRange(full, range, foreign);
    }

    /// <summary>
    /// Assembles a partial-range export over a single session (issue #3279, acceptance criteria 1-4).
    /// </summary>
    /// <param name="sessionId">The session to export.</param>
    /// <param name="range">The first/last entry selector, or <see langword="null"/> for the full session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result carrying the excerpt, or a specific rejection.</returns>
    public async Task<ExportRangeResult> AssembleSessionRangeAsync(
        SessionId sessionId,
        ExportRangeSelector? range,
        CancellationToken cancellationToken = default)
    {
        var full = await AssembleSessionAsync(sessionId, cancellationToken);
        if (full is null)
            return ExportRangeResult.SubjectNotFound();

        if (range is null)
            return ExportRangeResult.Success(full);

        var ownSessionIds = new HashSet<string>([sessionId.Value], StringComparer.Ordinal);
        var foreign = await ForeignSessionIdsAsync(range, ownSessionIds, cancellationToken);

        return ApplyRange(full, range, foreign);
    }

    /// <summary>
    /// Determines which of the two supplied endpoints name a session that exists but does not belong
    /// to the subject being exported (issue #3279, acceptance criterion 4).
    /// </summary>
    /// <remarks>
    /// Only the two endpoint sessions are probed - never the whole session table. Enumerating every
    /// session to answer a two-key question would make an export's cost grow with the size of the
    /// instance, which is exactly the full-table-scan pattern the session store's indexed queries
    /// exist to avoid.
    /// </remarks>
    private async Task<IReadOnlySet<string>> ForeignSessionIdsAsync(
        ExportRangeSelector range,
        HashSet<string> ownSessionIds,
        CancellationToken cancellationToken)
    {
        var foreign = new HashSet<string>(StringComparer.Ordinal);

        foreach (var endpoint in new[] { range.FirstEntryId, range.LastEntryId })
        {
            if (!ExportEntryId.TryGetSessionId(endpoint, out var sessionId))
                continue;
            if (ownSessionIds.Contains(sessionId) || foreign.Contains(sessionId))
                continue;

            GatewaySession? candidate;
            try
            {
                candidate = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An endpoint's session-id segment is caller-supplied text, so it need not be a
                // valid SessionId at all. A lookup that cannot even be attempted degrades the
                // rejection REASON from "foreign" to "not found" - which is still a rejection.
                continue;
            }

            if (candidate is not null)
                foreign.Add(sessionId);
        }

        return foreign;
    }

    /// <summary>
    /// Slices an assembled document to the selected range and recomputes every derived total over
    /// the slice (issue #3279, acceptance criterion 2).
    /// </summary>
    /// <remarks>
    /// The counts are recomputed from the sliced entry list, never carried over from the full
    /// document. Reusing the full-conversation totals while swapping only the entry list is the
    /// exact defect this issue exists to prevent: an excerpt of three messages whose header claims
    /// four hundred.
    /// </remarks>
    private static ExportRangeResult ApplyRange(
        ExportDocument full,
        ExportRangeSelector range,
        IReadOnlySet<string> foreignSessionIds)
    {
        var rejection = ExportRangeResolver.TryResolve(
            full.Entries, range, foreignSessionIds, out var firstIndex, out var lastIndex);
        if (rejection is not null)
            return rejection;

        var selected = full.Entries.Skip(firstIndex).Take(lastIndex - firstIndex + 1).ToList();
        var omitted = full.Entries.Count - selected.Count;

        // Session metadata is recomputed over the slice too, and sessions contributing nothing to
        // the range are dropped entirely - listing a session with "0 message(s)" in an excerpt
        // header describes the conversation, not the document the reader is holding.
        var sessionIdsInRange = selected.Select(e => e.SessionId).ToHashSet(StringComparer.Ordinal);
        var sessions = full.Sessions
            .Where(s => sessionIdsInRange.Contains(s.SessionId))
            .Select(s => s with { MessageCount = selected.Count(e => e.Kind == "message" && e.SessionId == s.SessionId) })
            .ToList();

        return ExportRangeResult.Success(full with
        {
            Scope = ExportScope.Excerpt,
            Range = range,
            Entries = selected,
            Sessions = sessions,
            MessageCount = CountMessages(selected),
            ToolCallCount = CountToolCalls(selected),
            OmittedEntryCount = omitted,
            OmissionNote = BuildOmissionNote(omitted)
        });
    }

    private static string BuildOmissionNote(int omittedEntryCount)
        => omittedEntryCount > 0
            ? $"Content was omitted: this is an excerpt of the full transcript. {omittedEntryCount} " +
              $"further {(omittedEntryCount == 1 ? "entry" : "entries")} outside the selected range " +
              "are not included in this document."
            : "Content was omitted: this document was produced from an explicit range selection. " +
              "The selected range covered the whole transcript, so no entries were dropped.";

    private static IReadOnlyList<ExportSessionInfo> BuildSessionInfos(
        IReadOnlyList<GatewaySession> sessions,
        IReadOnlyList<ConversationHistoryEntry> entries)
        => [.. sessions.Select(s => new ExportSessionInfo(
            s.SessionId.Value,
            s.AgentId.Value,
            s.CreatedAt,
            s.UpdatedAt,
            s.Status.ToString(),
            entries.Count(e => e.Kind == "message" && e.SessionId == s.SessionId.Value)))];

    private static int CountMessages(IReadOnlyList<ConversationHistoryEntry> entries)
        => entries.Count(e => e.Kind == "message");

    private static int CountToolCalls(IReadOnlyList<ConversationHistoryEntry> entries)
        => entries.Count(e => e.Kind == "message" && !string.IsNullOrEmpty(e.ToolName));
}
