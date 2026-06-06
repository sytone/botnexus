using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics.CodeAnalysis;
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
    private readonly IConversationResetService? _resetService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationsController"/> class.
    /// </summary>
    /// <param name="conversations">The conversation store.</param>
    /// <param name="sessions">The session store (used for history assembly).</param>
    /// <param name="conversationChangeNotifiers">Publishes conversation lifecycle notifications to connected channel clients.</param>
    /// <param name="logger">Logs best-effort transport notification failures.</param>
    /// <param name="askUserResponseRegistry">Optional registry used to cancel pending ask_user prompts on archive.</param>
    /// <param name="resetService">Canonical reset service. When supplied (the default in production DI), Archive
    /// and Reset endpoints delegate the active-session seal + memory flush + ask-user cancel to it. When omitted,
    /// the controller falls back to a best-effort in-place seal — used only by tests that construct the controller
    /// directly without DI.</param>
    public ConversationsController(
        IConversationStore conversations,
        ISessionStore sessions,
        IEnumerable<IConversationChangeNotifier>? conversationChangeNotifiers = null,
        ILogger<ConversationsController>? logger = null,
        IAskUserResponseRegistry? askUserResponseRegistry = null,
        IConversationResetService? resetService = null)
    {
        _conversations = conversations;
        _sessions = sessions;
        _conversationChangeNotifiers = conversationChangeNotifiers?.ToArray() ?? [];
        _logger = logger ?? NullLogger<ConversationsController>.Instance;
        _askUserResponseRegistry = askUserResponseRegistry;
        _resetService = resetService;
    }

    /// <summary>
    /// Lists conversations. With no <paramref name="agentId"/>, returns global active summaries
    /// (admin/debug view). With <paramref name="agentId"/>, returns conversations <em>relevant
    /// to</em> that agent: the union of (a) conversations the agent owns/initiated and (b)
    /// conversations where the agent appears as a participant (W-1 responder-side visibility,
    /// shipped in P9-G / issue #661). The union is materialised distinct-by-ConversationId so
    /// owner-and-participant conversations appear exactly once.
    /// </summary>
    /// <remarks>
    /// Only conversations with <see cref="ConversationStatus.Active"/> are returned in either
    /// mode. The <paramref name="agentId"/> branch resolves the citizen through
    /// <see cref="IConversationStore.ListForCitizenAsync"/> so the indexed participant lookup
    /// (SQLite <c>idx_conversation_participants_citizen</c>) is used; the result is projected
    /// to <see cref="ConversationSummary"/> in this controller. Results are ordered most
    /// recently updated first, with a deterministic <see cref="ConversationId"/> tie-breaker.
    /// </remarks>
    /// <param name="agentId">Optional. Filter to conversations relevant to this agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of conversation summaries.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ConversationSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult> List(
        [FromQuery] string? agentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            var allSummaries = await _conversations.GetSummariesAsync(cancellationToken);
            return Ok(allSummaries);
        }

        var citizen = CitizenId.Of(AgentId.From(agentId));
        var relevant = await _conversations.ListForCitizenAsync(citizen, cancellationToken);

        var summaries = relevant
            .Where(c => c.Status == ConversationStatus.Active)
            .OrderByDescending(c => c.UpdatedAt)
            .ThenBy(c => c.ConversationId.Value, StringComparer.Ordinal)
            .Select(ToSummary)
            .ToList();

        return Ok(summaries);
    }

    private static ConversationSummary ToSummary(Conversation c) =>
        new(
            c.ConversationId.Value,
            c.AgentId.Value,
            c.Title,
            c.IsDefault,
            c.Status.ToString(),
            c.ActiveSessionId?.Value,
            c.ChannelBindings.Count,
            c.CreatedAt,
            c.UpdatedAt,
            c.Purpose,
            c.Kind.ToString());

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
            UpdatedAt = DateTimeOffset.UtcNow,
            // Initiator left null: this endpoint is currently unauthenticated, so we cannot
            // determine which user/agent created the conversation. Once SignalR/portal claims-based
            // auth lands (see issue #527) this should be populated from the request principal.
            Initiator = null
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

        // Security (#615 critique): refuse to modify resolver-owned legacy conversations
        // through the public REST surface. These rows are created by
        // LegacyConversationResolver to group orphan sessions and are injected into the
        // agent's system prompt via SystemPromptBuilder. Allowing arbitrary REST callers
        // to overwrite Title/Purpose/Instructions would let an attacker inject prompt
        // content into the agent's trusted context (XPIA). The identifying signature is
        // Title == "legacy:{agentId}" AND Initiator == CitizenId.Of(agentId) — only the
        // resolver can produce that combination because POST /api/conversations leaves
        // Initiator = null.
        if (IsResolverOwnedLegacyConversation(conversation))
        {
            return BadRequest(new
            {
                error = "legacy conversations are managed by the system and cannot be modified."
            });
        }

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

    private static bool IsResolverOwnedLegacyConversation(Conversation conversation)
    {
        if (!conversation.Title.StartsWith("legacy:", StringComparison.Ordinal))
            return false;
        if (conversation.Initiator is not { } initiator)
            return false;
        return initiator == CitizenId.Of(conversation.AgentId);
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

        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();

        // Get all sessions belonging to this conversation, ordered by CreatedAt ascending.
        // ListByConversationAsync guarantees Active+Sealed inclusion + the ordering contract,
        // and goes through the indexed Sqlite path -- no full-table scan (F-7).
        var convId = ConversationId.From(conversationId);
        var linkedSessions = await _sessions.ListByConversationAsync(convId, cancellationToken: cancellationToken);

        // Fallback for #732: cron sessions and sessions created before the conversation-linkage
        // migration may have conversation_id = NULL in the sessions table. ListByConversationAsync
        // filters on that column and returns nothing, leaving the history endpoint empty even
        // though the conversation has messages. When the indexed query returns no sessions,
        // fall back to loading conversation.ActiveSessionId directly — provided it is not already
        // included in the linked set (dedup guard).
        if (linkedSessions.Count == 0 && conversation.ActiveSessionId is { } fallbackSessionId)
        {
            var fallbackSession = await _sessions.GetAsync(fallbackSessionId, cancellationToken);
            if (fallbackSession is not null)
                linkedSessions = [fallbackSession];
        }

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

            // Append all history entries from this session.
            // Skip assistant entries whose content is exactly "NO_REPLY" (optionally padded with whitespace).
            // These are deliberate cron no-ops that produced no user-facing output; including them in
            // history would show blank turns in the portal for every cron wakeup that had nothing to say (#773).
            var snapshot = session.GetHistorySnapshot();
            foreach (var entry in snapshot)
            {
                if (entry.Role == MessageRole.Assistant &&
                    string.Equals(entry.Content?.Trim(), "NO_REPLY", StringComparison.Ordinal))
                    continue;

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
    /// <remarks>
    /// Performs the canonical reset on the active session (flush memory, cancel pending ask_user
    /// prompts, seal the session via <c>Status=Sealed + SaveAsync</c>, clear <see cref="Conversation.ActiveSessionId"/>)
    /// before archiving the conversation. This closes F-2c — the REST archive path used to skip
    /// the memory flush, so the agent had no chance to write a memory bridge before the session
    /// was sealed.
    /// </remarks>
    [HttpDelete("{conversationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Archive(string conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();

        // Canonical reset of the active session (stop supervisor + flush memory bridge +
        // cancel pending ask_user prompts + seal via SaveAsync + clear ActiveSessionId).
        // No expectedActiveSessionId — the REST caller doesn't know which session is active.
        if (_resetService is not null)
        {
            await _resetService.ResetActiveSessionAsync(conversation.ConversationId, cancellationToken: cancellationToken);
        }
        else
        {
            // Defensive fallback used only when DI omitted the service (legacy test harnesses).
            // Misses the memory-flush bridge, which is the F-2c bug — production DI must wire it.
            if (conversation.ActiveSessionId is { } activeSessionId)
            {
                await SealSessionAsync(activeSessionId, cancellationToken);
            }

            _askUserResponseRegistry?.CancelAllForConversation(conversation.ConversationId);
        }

        await _conversations.ArchiveAsync(conversation.ConversationId, cancellationToken);
        await NotifyConversationChangedBestEffortAsync("archived", conversation.AgentId.Value, conversation.ConversationId.Value, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Resets the active session of a conversation without archiving the conversation itself.
    /// Equivalent to the SignalR <c>ResetSession</c> hub method: stops the supervisor, flushes
    /// the session-end memory bridge, cancels pending ask_user prompts, seals the active session
    /// in place, and clears <see cref="Conversation.ActiveSessionId"/> so the next inbound message
    /// starts a fresh session inside the same conversation with the system prompt re-injected.
    /// </summary>
    /// <param name="conversationId">The conversation to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <list type="bullet">
    ///   <item><description><c>200 OK</c> with the reset outcome and (when applicable) the sealed session id.</description></item>
    ///   <item><description><c>404 NotFound</c> when the conversation does not exist.</description></item>
    ///   <item><description><c>503 ServiceUnavailable</c> when the reset service is not registered (DI misconfiguration).</description></item>
    /// </list>
    /// </returns>
    [HttpPost("{conversationId}/reset")]
    [ProducesResponseType(typeof(ConversationResetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Reset(string conversationId, CancellationToken cancellationToken)
    {
        if (_resetService is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Conversation reset service is not configured.");

        var result = await _resetService.ResetActiveSessionAsync(ConversationId.From(conversationId), cancellationToken: cancellationToken);

        if (result.Outcome == ConversationResetOutcome.NotFound)
            return NotFound();

        return Ok(new ConversationResetResponse(
            ConversationId: conversationId,
            Outcome: result.Outcome.ToString(),
            SealedSessionId: result.SealedSessionId?.Value));
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

}

