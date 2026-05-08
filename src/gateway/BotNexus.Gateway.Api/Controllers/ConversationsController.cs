using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for conversation management — listing, creating, updating, and inspecting conversations
/// along with their channel bindings and assembled history.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationStore _conversations;
    private readonly ISessionStore _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationsController"/> class.
    /// </summary>
    /// <param name="conversations">The conversation store.</param>
    /// <param name="sessions">The session store (used for history assembly).</param>
    public ConversationsController(IConversationStore conversations, ISessionStore sessions)
    {
        _conversations = conversations;
        _sessions = sessions;
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
        AgentId? parsedAgentId = string.IsNullOrWhiteSpace(agentId) ? null : AgentId.From(agentId);
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
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var created = await _conversations.CreateAsync(conversation, cancellationToken);
        return CreatedAtAction(nameof(Get), new { conversationId = created.ConversationId.Value }, ToResponse(created));
    }

    /// <summary>
    /// Updates the title of an existing conversation.
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
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { error = "title must not be empty." });

        if (request.Title!.Length > 500)
            return BadRequest(new { error = "title must be 500 characters or fewer." });

        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();

        conversation.Title = request.Title;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await _conversations.SaveAsync(conversation, cancellationToken);
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
        List<ConversationHistoryEntry> page;
        if (offset >= totalCount)
        {
            page = [];
        }
        else
        {
            // Page from newest entries so refreshes include the latest turns even when
            // conversations have more than one page of history.
            var take = Math.Min(boundedLimit, totalCount - offset);
            var startIndex = Math.Max(0, totalCount - offset - take);
            page = allEntries
                .Skip(startIndex)
                .Take(take)
                .ToList();
        }

        return Ok(new ConversationHistoryResponse(
            ConversationId: conversationId,
            TotalCount: totalCount,
            Offset: offset,
            Limit: boundedLimit,
            Entries: page));
    }

    /// <summary>Archives a conversation (soft delete).</summary>
    [HttpDelete("{conversationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Archive(string conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null) return NotFound();
        await _conversations.ArchiveAsync(ConversationId.From(conversationId), cancellationToken);
        return NoContent();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ConversationResponse ToResponse(Conversation c) => new(
        ConversationId: c.ConversationId.Value,
        AgentId: c.AgentId.Value,
        Title: c.Title,
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

}
