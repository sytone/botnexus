using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SessionId = BotNexus.Domain.Primitives.SessionId;
using SessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for conversation management ΓÇö listing, creating, updating, and inspecting conversations
/// along with their channel bindings and assembled history.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationStore _conversations;
    private readonly ISessionStore _sessions;
    private readonly IReadOnlyList<IConversationChangeNotifier> _conversationChangeNotifiers;
    private readonly ILogger<ConversationsController> _logger;
    private readonly IAskUserResponseRegistry? _askUserResponseRegistry;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationsController"/> class.
    /// </summary>
    /// <param name="conversations">The conversation store.</param>
    /// <param name="sessions">The session store (used for history assembly).</param>
    /// <param name="conversationChangeNotifiers">Publishes conversation lifecycle notifications to connected channel clients.</param>
    /// <param name="logger">Logs best-effort transport notification failures.</param>
    /// <param name="askUserResponseRegistry">Optional registry used to cancel pending ask_user prompts on archive.</param>
    public ConversationsController(
        IConversationStore conversations,
        ISessionStore sessions,
        IEnumerable<IConversationChangeNotifier>? conversationChangeNotifiers = null,
        ILogger<ConversationsController>? logger = null,
        IAskUserResponseRegistry? askUserResponseRegistry = null)
    {
        _conversations = conversations;
        _sessions = sessions;
        _conversationChangeNotifiers = conversationChangeNotifiers?.ToArray() ?? [];
        _logger = logger ?? NullLogger<ConversationsController>.Instance;
        _askUserResponseRegistry = askUserResponseRegistry;
    }

    /// <summary>
    /// Lists all conversations, optionally filtered by agent ID.
    /// </summary>
    /// <param name="agentId">If specified, returns only conversations for this agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of conversation summaries.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ConversationSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult> List(
        [FromQuery] string? agentId,
        CancellationToken cancellationToken)
    {
        AgentId? parsedAgentId = string.IsNullOrWhiteSpace(agentId) ? (AgentId?)null : AgentId.From(agentId);
        var summaries = await _conversations.GetSummariesAsync(parsedAgentId, cancellationToken);
        return Ok(summaries);
    }

    /// <summary>
    /// Gets a specific conversation by ID, including all channel bindings.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Full conversation detail, or 404 if not found.</returns>
    [HttpGet("{conversationId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(
        string conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();

        return Ok(ToResponse(conversation));
    }

    /// <summary>
    /// Creates a new conversation.
    /// </summary>
    /// <param name="request">The create request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the created conversation.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> Create(
        [FromBody] CreateConversationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId))
            return BadRequest(new { error = "agentId is required." });

        var conversation = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From(request.AgentId),
            Title = string.IsNullOrWhiteSpace(request.Title) ? "New conversation" : request.Title,
            Purpose = NormalizePurpose(request.Purpose),
            Instructions = NormalizeInstructions(request.Instructions),
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var created = await _conversations.CreateAsync(conversation, cancellationToken);
        await NotifyConversationChangedBestEffortAsync("created", created.AgentId.Value, created.ConversationId.Value, cancellationToken);
        return CreatedAtAction(nameof(Get), new { conversationId = created.ConversationId.Value }, ToResponse(created));
    }

    /// <summary>
    /// Updates editable metadata for an existing conversation.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="request">The patch request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated conversation, or 404 if not found.</returns>
    [HttpPatch("{conversationId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Patch(
        string conversationId,
        [FromBody] PatchConversationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Title is null && request.Purpose is null && request.Instructions is null)
            return BadRequest(new { error = "title or purpose is required." });

        if (request.Title is not null && string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { error = "title must not be empty." });

        if (request.Title is { Length: > 500 })
            return BadRequest(new { error = "title must be 500 characters or fewer." });

        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();

        if (request.Title is not null)
            conversation.Title = request.Title;
        if (request.Purpose is not null)
            conversation.Purpose = NormalizePurpose(request.Purpose);
        if (request.Instructions is not null)
            conversation.Instructions = NormalizeInstructions(request.Instructions);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await _conversations.SaveAsync(conversation, cancellationToken);
        await NotifyConversationChangedBestEffortAsync("updated", conversation.AgentId.Value, conversation.ConversationId.Value, cancellationToken);
        return Ok(ToResponse(conversation));
    }

    /// <summary>
    /// Adds a channel binding to an existing conversation.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="request">The binding request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the created binding, or 404 if conversation not found.</returns>
    [HttpPost("{conversationId}/bindings")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AddBinding(
        string conversationId,
        [FromBody] AddBindingRequest request,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();

        if (!Enum.TryParse<BindingMode>(request.Mode, ignoreCase: true, out var bindingMode))
            bindingMode = BindingMode.Interactive;

        if (!Enum.TryParse<ThreadingMode>(request.ThreadingMode, ignoreCase: true, out var threadingMode))
            threadingMode = ThreadingMode.Single;

        var binding = new ChannelBinding
        {
            BindingId = BindingId.Create(),
            ChannelType = ChannelKey.From(request.ChannelType),
            ChannelAddress = ChannelAddress.From(request.ChannelAddress ?? string.Empty),
            ThreadId = ThreadId.FromNullable(request.ThreadId),
            Mode = bindingMode,
            ThreadingMode = threadingMode,
            DisplayPrefix = request.DisplayPrefix,
            BoundAt = DateTimeOffset.UtcNow
        };

        conversation.ChannelBindings.Add(binding);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await _conversations.SaveAsync(conversation, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ToBindingResponse(binding));
    }

    /// <summary>
    /// Removes a channel binding from a conversation.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="bindingId">The binding identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 No Content, or 404 if conversation or binding not found.</returns>
    [HttpDelete("{conversationId}/bindings/{bindingId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RemoveBinding(
        string conversationId,
        string bindingId,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();

        var binding = conversation.ChannelBindings.FirstOrDefault(b =>
            string.Equals(b.BindingId.Value, bindingId, StringComparison.Ordinal));
        if (binding is null)
            return NotFound();

        conversation.ChannelBindings.Remove(binding);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await _conversations.SaveAsync(conversation, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Returns assembled conversation history across all sessions linked to this conversation,
    /// ordered chronologically with session boundary markers between sessions.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="limit">Maximum number of entries to return (default 50, max 200).</param>
    /// <param name="offset">
    /// Zero-based offset from the most recent entry. <c>offset=0</c> returns the latest page,
    /// larger offsets page backwards into older history.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paginated history response, or 404 if conversation not found.</returns>
    [HttpGet("{conversationId}/history")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetHistory(
        string conversationId,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
            return BadRequest(new { error = "offset must be >= 0." });
        if (limit <= 0)
            return BadRequest(new { error = "limit must be > 0." });

        var boundedLimit = Math.Min(limit, 200);
        if (TryParseVirtualCronConversationId(conversationId, out var virtualSessionId))
            return await GetVirtualCronHistoryAsync(conversationId, virtualSessionId, boundedLimit, offset, cancellationToken);

        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();

        // Get all sessions belonging to this conversation, ordered by CreatedAt ascending
        var allSessions = await _sessions.ListAsync(cancellationToken: cancellationToken);
        var convId = ConversationId.From(conversationId);
        var linkedSessions = allSessions
            .Where(s => s.Session.ConversationId.HasValue &&
                        s.Session.ConversationId.Value == convId)
            .OrderBy(s => s.CreatedAt)
            .ToList();

        // Assemble flat list of history entries with boundary markers between sessions
        var allEntries = new List<ConversationHistoryEntry>();

        for (var i = 0; i < linkedSessions.Count; i++)
        {
            var session = linkedSessions[i];

            // Insert boundary marker before each session except the first
            if (i > 0)
            {
                var previousSession = linkedSessions[i - 1];
                allEntries.Add(new ConversationHistoryEntry
                {
                    Kind = "boundary",
                    SessionId = previousSession.SessionId.Value,
                    Timestamp = previousSession.UpdatedAt,
                    Reason = "session_end"
                });
            }

            // Append all history entries from this session
            var snapshot = session.GetHistorySnapshot();
            foreach (var entry in snapshot)
            {
                allEntries.Add(new ConversationHistoryEntry
                {
                    Kind = "message",
                    SessionId = session.SessionId.Value,
                    Role = entry.Role.ToString().ToLowerInvariant(),
                    Content = entry.Content,
                    Timestamp = entry.Timestamp,
                    ToolName = entry.ToolName,
                    ToolCallId = entry.ToolCallId,
                    ToolArgs = entry.ToolArgs,
                    ToolIsError = entry.ToolIsError
                });
            }
        }

        var totalCount = allEntries.Count;
        var page = PageFromNewest(allEntries, boundedLimit, offset);

        return Ok(new ConversationHistoryResponse(
            ConversationId: conversationId,
            TotalCount: totalCount,
            Offset: offset,
            Limit: boundedLimit,
            Entries: page));
    }

    /// <summary>
    /// Closes a conversation by archiving it (soft delete).
    /// Archived conversations are hidden from active listings and can be reopened automatically
    /// if a bound channel or explicit conversation id starts activity again.
    /// </summary>
    [HttpDelete("{conversationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Archive(string conversationId, CancellationToken cancellationToken)
    {
        SessionId? virtualCronSessionId = null;
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null && TryParseVirtualCronConversationId(conversationId, out var virtualSessionId))
        {
            virtualCronSessionId = virtualSessionId;
            var virtualSession = await _sessions.GetAsync(virtualSessionId, cancellationToken);
            if (virtualSession is null)
                return NoContent();

            if (virtualSession.Session.ConversationId is { } linkedConversationId)
                conversation = await _conversations.GetAsync(linkedConversationId, cancellationToken);

            if (conversation is null)
            {
                await SealSessionAsync(virtualSessionId, cancellationToken);
                return NoContent();
            }
        }

        if (conversation is null)
            return NotFound();

        if (virtualCronSessionId.HasValue)
            await SealSessionAsync(virtualCronSessionId.Value, cancellationToken);

        if (conversation.ActiveSessionId is { } activeSessionId)
            if (!virtualCronSessionId.HasValue || virtualCronSessionId.Value != activeSessionId)
                await SealSessionAsync(activeSessionId, cancellationToken);

        await SealConversationSessionsAsync(conversation.ConversationId, cancellationToken);

        await _conversations.ArchiveAsync(conversation.ConversationId, cancellationToken);
        _askUserResponseRegistry?.CancelAllForConversation(conversation.ConversationId);
        await NotifyConversationChangedBestEffortAsync("archived", conversation.AgentId.Value, conversation.ConversationId.Value, cancellationToken);
        return NoContent();
    }

    // ΓöÇΓöÇ Notification helpers ΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇ

    private async Task NotifyConversationChangedBestEffortAsync(string changeType, string agentId, string conversationId, CancellationToken cancellationToken)
    {
        if (_conversationChangeNotifiers.Count == 0)
            return;

        foreach (var notifier in _conversationChangeNotifiers)
        {
            try
            {
                await notifier.NotifyConversationChangedAsync(changeType, agentId, conversationId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to publish conversation change notification ({ChangeType}) for conversation {ConversationId} via notifier {NotifierType}.",
                    changeType,
                    conversationId,
                    notifier.GetType().FullName);
            }
        }
    }
    // ΓöÇΓöÇ Helpers ΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇ

    private static ConversationResponse ToResponse(Conversation c) => new(
        ConversationId: c.ConversationId.Value,
        AgentId: c.AgentId.Value,
        Title: c.Title,
        Purpose: c.Purpose,
        Instructions: c.Instructions,
        IsDefault: c.IsDefault,
        Status: c.Status.ToString(),
        ActiveSessionId: c.ActiveSessionId?.Value,
        Bindings: c.ChannelBindings.Select(ToBindingResponse).ToList(),
        CreatedAt: c.CreatedAt,
        UpdatedAt: c.UpdatedAt);

    private static BindingResponse ToBindingResponse(ChannelBinding b) => new(
        BindingId: b.BindingId.Value,
        ChannelType: b.ChannelType.Value,
        ChannelAddress: b.ChannelAddress.Value,
        ThreadId: b.ThreadId?.Value,
        Mode: b.Mode.ToString(),
        ThreadingMode: b.ThreadingMode.ToString(),
        DisplayPrefix: b.DisplayPrefix,
        BoundAt: b.BoundAt);

    private static string? NormalizePurpose(string? purpose)
        => string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim();

    private static string? NormalizeInstructions(string? instructions)
        => string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim();

    private async Task SealSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null || session.Status == SessionStatus.Sealed)
            return;

        session.Status = SessionStatus.Sealed;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _sessions.SaveAsync(session, cancellationToken);
    }

    private async Task SealConversationSessionsAsync(ConversationId conversationId, CancellationToken cancellationToken)
    {
        var allSessions = await _sessions.ListAsync(cancellationToken: cancellationToken);
        var linkedSessionIds = allSessions
            .Where(session => session.Session.ConversationId is { } linkedConversationId &&
                              linkedConversationId == conversationId)
            .Select(session => session.SessionId)
            .ToList();

        foreach (var sessionId in linkedSessionIds)
            await SealSessionAsync(sessionId, cancellationToken);
    }

    private async Task<ActionResult> GetVirtualCronHistoryAsync(
        string virtualConversationId,
        SessionId virtualSessionId,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(virtualSessionId, cancellationToken);
        if (session is null)
        {
            return Ok(new ConversationHistoryResponse(
                ConversationId: virtualConversationId,
                TotalCount: 0,
                Offset: offset,
                Limit: limit,
                Entries: []));
        }

        var allEntries = session.GetHistorySnapshot()
            .Select(entry => new ConversationHistoryEntry
            {
                Kind = "message",
                SessionId = session.SessionId.Value,
                Role = entry.Role.ToString().ToLowerInvariant(),
                Content = entry.Content,
                Timestamp = entry.Timestamp,
                ToolName = entry.ToolName,
                ToolCallId = entry.ToolCallId,
                ToolArgs = entry.ToolArgs,
                ToolIsError = entry.ToolIsError
            })
            .ToList();

        return Ok(new ConversationHistoryResponse(
            ConversationId: virtualConversationId,
            TotalCount: allEntries.Count,
            Offset: offset,
            Limit: limit,
            Entries: PageFromNewest(allEntries, limit, offset)));
    }

    private static List<ConversationHistoryEntry> PageFromNewest(
        IReadOnlyList<ConversationHistoryEntry> allEntries,
        int limit,
        int offset)
    {
        var totalCount = allEntries.Count;
        if (offset >= totalCount)
            return [];

        // Page from newest entries so refreshes include the latest turns even when
        // conversations have more than one page of history.
        var take = Math.Min(limit, totalCount - offset);
        var startIndex = Math.Max(0, totalCount - offset - take);
        return allEntries
            .Skip(startIndex)
            .Take(take)
            .ToList();
    }
    /// <summary>Gets the current canvas HTML for a conversation.</summary>
    [HttpGet("~/api/agents/{agentId}/conversations/{conversationId}/canvas")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetCanvas(string agentId, string conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return NotFound();
        if (string.IsNullOrEmpty(conversation.CanvasHtml))
            return NoContent();
        return Content(conversation.CanvasHtml, "text/html");
    }

    /// <summary>Saves canvas HTML for a conversation.</summary>
    [HttpPut("~/api/agents/{agentId}/conversations/{conversationId}/canvas")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PutCanvas(string agentId, string conversationId, [FromBody] string html, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return NotFound();
        conversation.CanvasHtml = string.IsNullOrEmpty(html) ? null : html;
        await _conversations.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private static bool TryParseVirtualCronConversationId(string conversationId, out SessionId sessionId)
    {
        const string prefix = "cron-session:";
        if (conversationId.StartsWith(prefix, StringComparison.Ordinal) &&
            conversationId.Length > prefix.Length)
        {
            sessionId = SessionId.From(conversationId[prefix.Length..]);
            return true;
        }

        sessionId = default;
        return false;
    }

}

