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
