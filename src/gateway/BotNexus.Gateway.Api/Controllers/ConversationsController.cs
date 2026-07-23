using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Conversations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Resolution;
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
    private readonly IConversationAuditLog? _auditLog;
    private readonly IConversationHistoryAssembler _historyAssembler;
    private readonly ModelRegistry? _modelRegistry;
    private readonly IAgentRegistry? _agentRegistry;

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
    /// <param name="auditLog">Optional audit log for recording conversation mutations.</param>
    /// <param name="historyAssembler">Assembles cross-session conversation history. When omitted (legacy
    /// test harnesses constructing the controller directly), a default instance over the same stores is used.</param>
    /// <param name="modelRegistry">Optional model registry used to validate per-conversation model / thinking / context overrides against real model capabilities. When omitted, override values are stored without capability validation.</param>
    /// <param name="agentRegistry">Optional agent registry used to resolve the owning agent's provider and default model when validating overrides.</param>
    public ConversationsController(
        IConversationStore conversations,
        ISessionStore sessions,
        IEnumerable<IConversationChangeNotifier>? conversationChangeNotifiers = null,
        ILogger<ConversationsController>? logger = null,
        IAskUserResponseRegistry? askUserResponseRegistry = null,
        IConversationResetService? resetService = null,
        IConversationAuditLog? auditLog = null,
        IConversationHistoryAssembler? historyAssembler = null,
        ModelRegistry? modelRegistry = null,
        IAgentRegistry? agentRegistry = null)
    {
        _conversations = conversations;
        _sessions = sessions;
        _conversationChangeNotifiers = conversationChangeNotifiers?.ToArray() ?? [];
        _logger = logger ?? NullLogger<ConversationsController>.Instance;
        _askUserResponseRegistry = askUserResponseRegistry;
        _resetService = resetService;
        _auditLog = auditLog;
        // When DI omits the assembler (legacy test harnesses that construct the controller
        // directly), fall back to the default implementation over the same stores so the
        // history endpoint keeps working without an explicit registration.
        _historyAssembler = historyAssembler ?? new ConversationHistoryAssembler(conversations, sessions);
        _modelRegistry = modelRegistry;
        _agentRegistry = agentRegistry;
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
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.PinnedAt)
            .ThenByDescending(c => c.UpdatedAt)
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
            c.Kind.ToString(),
            c.IsPinned,
            c.PinnedAt,
            c.Participants.Select(p => new ParticipantSummary(
                p.CitizenId.Kind.ToString(),
                p.CitizenId.Value,
                p.Role)).ToList());

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

        if (ConversationInputValidator.ValidateTitle(request.Title) is { } titleError)
            return BadRequest(new { error = titleError });
        if (ConversationInputValidator.ValidatePurpose(request.Purpose) is { } purposeError)
            return BadRequest(new { error = purposeError });
        if (ConversationInputValidator.ValidateInstructions(request.Instructions) is { } instructionsError)
            return BadRequest(new { error = instructionsError });

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
        await AuditAsync(created.ConversationId.Value, "created", "api", "rest-api", null, created.Title, cancellationToken);
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

        if (request.Title is not null)
        {
            if (ConversationInputValidator.ValidateTitle(request.Title, required: true) is { } titleError)
                return BadRequest(new { error = titleError });
        }
        if (ConversationInputValidator.ValidatePurpose(request.Purpose) is { } purposeError)
            return BadRequest(new { error = purposeError });
        if (ConversationInputValidator.ValidateInstructions(request.Instructions) is { } instructionsError)
            return BadRequest(new { error = instructionsError });

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
        {
            var prevTitle = conversation.Title;
            conversation.Title = request.Title;
            await AuditAsync(conversationId, "title_changed", "api", "rest-api", prevTitle, request.Title, cancellationToken);
        }
        if (request.Purpose is not null)
        {
            var prevPurpose = conversation.Purpose;
            conversation.Purpose = NormalizePurpose(request.Purpose);
            await AuditAsync(conversationId, "purpose_set", "api", "rest-api", prevPurpose, conversation.Purpose, cancellationToken);
        }
        if (request.Instructions is not null)
        {
            var prevInstructions = conversation.Instructions;
            conversation.Instructions = NormalizeInstructions(request.Instructions);
            await AuditAsync(conversationId, "instructions_set", "api", "rest-api", prevInstructions, conversation.Instructions, cancellationToken);
        }
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

        // History assembly (cross-session listing, #732 fallback, boundary markers, NO_REPLY/fold
        // filtering, compaction projection, newest-first paging) lives in the assembler so it can
        // be unit-tested in isolation and reused by the SignalR/portal path. The controller's job
        // here is validate -> delegate -> map.
        var result = await _historyAssembler.AssembleAsync(
            ConversationId.From(conversationId),
            boundedLimit,
            offset,
            cancellationToken);

        return result is null ? NotFound() : Ok(result);
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
        // Best-effort reset (#1696): a slow supervisor-stop or memory-flush stall must not turn a
        // DELETE into a 500. Swallow cancellation/timeout (and any reset failure) here and proceed
        // to archive anyway; the session is sealed lazily on next inbound if the reset did not finish.
        if (_resetService is not null)
        {
            try
            {
                await _resetService.ResetActiveSessionAsync(conversation.ConversationId, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Best-effort active-session reset failed for conversation {ConversationId}; archiving anyway.", conversation.ConversationId.Value);
            }
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

        await _conversations.ArchiveAsync(conversation.ConversationId, "rest-api", HttpContext?.TraceIdentifier ?? System.Diagnostics.Activity.Current?.Id, "api", cancellationToken);
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
        UpdatedAt: c.UpdatedAt,
        ModelOverride: c.ModelOverride,
        ThinkingOverride: c.ThinkingOverride,
        ContextWindowOverride: c.ContextWindowOverride);

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

    /// <summary>
    /// Gets the current per-conversation todo state (the raw <c>TodoJson</c> payload) for a
    /// conversation, so the portal Todo panel can hydrate on initial load (#1464 step 5).
    /// Returns 204 when the conversation has no todo state.
    /// </summary>
    [HttpGet("~/api/agents/{agentId}/conversations/{conversationId}/todo")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetTodo(string agentId, string conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return NotFound();
        if (string.IsNullOrEmpty(conversation.TodoJson))
            return NoContent();
        return Content(conversation.TodoJson, "application/json");
    }

    /// <summary>
    /// Gets the current pending <c>ask_user</c> prompt (the raw <c>PendingAskUserJson</c> payload, a
    /// serialized <c>AskUserRequest</c>) for a conversation, so a reloaded tab, a newly-opened window,
    /// or a client that missed the live <c>UserInputRequired</c> event can hydrate the prompt on
    /// connect (ask_user durability, #1488). Returns 204 when no prompt is waiting.
    /// </summary>
    [HttpGet("~/api/agents/{agentId}/conversations/{conversationId}/pending-ask-user")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetPendingAskUser(string agentId, string conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return NotFound();
        if (string.IsNullOrEmpty(conversation.PendingAskUserJson))
            return NoContent();
        return Content(conversation.PendingAskUserJson, "application/json");
    }

    /// <summary>
    /// Sets or clears the per-conversation model / thinking / context override (PBI5, issue #1706).
    /// Each field is applied independently: a non-null value sets that override, a null value clears
    /// it back to the agent default. Requested overrides are validated against the resolved model's
    /// capabilities (reasoning support, top thinking tier, maximum context window) before being
    /// persisted, so an override the model cannot express is rejected with 400 rather than silently
    /// stored. The persisted override is consumed as the top precedence layer by
    /// <c>ModelOverrideResolver</c> when the next session in this conversation starts.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="request">The override request body. Fields left null clear that override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated conversation, 400 when an override is invalid, or 404 if not found.</returns>
    [HttpPut("{conversationId}/override")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetOverride(
        string conversationId,
        [FromBody] SetConversationOverrideRequest request,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();

        if (IsResolverOwnedLegacyConversation(conversation))
            return BadRequest(new { error = "legacy conversations are managed by the system and cannot be modified." });

        // The requested (or already-persisted) model id determines which capability set we validate
        // the thinking / context overrides against. Prefer the incoming model override, then any
        // existing conversation override, then the agent's configured default.
        var effectiveModelId = NormalizeOverrideString(request.Model)
            ?? conversation.ModelOverride
            ?? _agentRegistry?.Get(conversation.AgentId)?.ModelId;

        var resolvedModel = ResolveModelForValidation(conversation.AgentId, effectiveModelId);

        // Model override: only validate that the requested id is registered/known when we can.
        if (NormalizeOverrideString(request.Model) is { } requestedModelId
            && _modelRegistry is not null
            && resolvedModel is null)
        {
            return BadRequest(new { error = $"Model '{requestedModelId}' is not registered for this agent's provider." });
        }

        // Thinking override: parse the wire token and validate against model capabilities.
        if (NormalizeOverrideString(request.Thinking) is { } thinkingToken)
        {
            if (!TryParseThinkingLevel(thinkingToken, out var thinking))
                return BadRequest(new { error = $"Unknown thinking level '{thinkingToken}'." });
            if (resolvedModel is not null
                && ConversationOverrideValidator.ValidateThinking(resolvedModel, thinking) is { IsValid: false } tErr)
                return BadRequest(new { error = tErr.Error });
        }

        // Context override: validate against the model's maximum context window.
        if (request.ContextWindow is { } contextWindow)
        {
            if (resolvedModel is not null
                && ConversationOverrideValidator.ValidateContextWindow(resolvedModel, contextWindow) is { IsValid: false } cErr)
                return BadRequest(new { error = cErr.Error });
            else if (contextWindow <= 0)
                return BadRequest(new { error = "Context window override must be a positive number of tokens." });
        }

        var prevModel = conversation.ModelOverride;
        var prevThinking = conversation.ThinkingOverride;
        var prevContext = conversation.ContextWindowOverride;

        conversation.ModelOverride = NormalizeOverrideString(request.Model);
        conversation.ThinkingOverride = NormalizeOverrideString(request.Thinking);
        conversation.ContextWindowOverride = request.ContextWindow;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await _conversations.SaveAsync(conversation, cancellationToken);

        await AuditAsync(conversationId, "model_override_set", "api", "rest-api", prevModel, conversation.ModelOverride, cancellationToken);
        await AuditAsync(conversationId, "thinking_override_set", "api", "rest-api", prevThinking, conversation.ThinkingOverride, cancellationToken);
        await AuditAsync(conversationId, "context_override_set", "api", "rest-api", prevContext?.ToString(), conversation.ContextWindowOverride?.ToString(), cancellationToken);
        await NotifyConversationChangedBestEffortAsync("updated", conversation.AgentId.Value, conversation.ConversationId.Value, cancellationToken);
        return Ok(ToResponse(conversation));
    }

    /// <summary>Clears every per-conversation override, reverting all three fields to the agent default.</summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated conversation, or 404 if not found.</returns>
    [HttpDelete("{conversationId}/override")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ClearOverride(string conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();

        var prevModel = conversation.ModelOverride;
        conversation.ModelOverride = null;
        conversation.ThinkingOverride = null;
        conversation.ContextWindowOverride = null;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await _conversations.SaveAsync(conversation, cancellationToken);
        await AuditAsync(conversationId, "model_override_cleared", "api", "rest-api", prevModel, null, cancellationToken);
        await NotifyConversationChangedBestEffortAsync("updated", conversation.AgentId.Value, conversation.ConversationId.Value, cancellationToken);
        return Ok(ToResponse(conversation));
    }

    private LlmModel? ResolveModelForValidation(AgentId agentId, string? modelId)
    {
        if (_modelRegistry is null || string.IsNullOrWhiteSpace(modelId))
            return null;
        var provider = _agentRegistry?.Get(agentId)?.ApiProvider;
        if (string.IsNullOrWhiteSpace(provider))
            return null;
        return _modelRegistry.GetModel(provider, modelId);
    }

    private static bool TryParseThinkingLevel(string token, out ThinkingLevel level)
    {
        switch (token.Trim().ToLowerInvariant())
        {
            case "minimal": level = ThinkingLevel.Minimal; return true;
            case "low": level = ThinkingLevel.Low; return true;
            case "medium": level = ThinkingLevel.Medium; return true;
            case "high": level = ThinkingLevel.High; return true;
            case "xhigh": level = ThinkingLevel.ExtraHigh; return true;
            case "max": level = ThinkingLevel.Max; return true;
            default: level = default; return false;
        }
    }

    private static string? NormalizeOverrideString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Pins a conversation to the top of the list.</summary>
    [HttpPost("{conversationId}/pin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Pin(string conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null) return NotFound();
        await _conversations.PinAsync(ConversationId.From(conversationId), true, cancellationToken);
        await NotifyConversationChangedBestEffortAsync("updated", conversation.AgentId.Value, conversationId, cancellationToken);
        return NoContent();
    }

    /// <summary>Unpins a conversation from the top of the list.</summary>
    [HttpDelete("{conversationId}/pin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Unpin(string conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null) return NotFound();
        await _conversations.PinAsync(ConversationId.From(conversationId), false, cancellationToken);
        await NotifyConversationChangedBestEffortAsync("updated", conversation.AgentId.Value, conversationId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Returns the audit log for a conversation.
    /// </summary>
    [HttpGet("{conversationId}/audit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetAudit(
        string conversationId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (_auditLog is null)
            return Ok(Array.Empty<ConversationAuditEntry>());

        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();

        var entries = await _auditLog.GetAsync(conversationId, Math.Min(limit, 200), cancellationToken);
        return Ok(entries);
    }

    private async Task AuditAsync(string conversationId, string action, string actor, string source, string? previousValue, string? newValue, CancellationToken ct)
    {
        if (_auditLog is null)
            return;

        try
        {
            await _auditLog.LogAsync(new ConversationAuditEntry
            {
                ConversationId = conversationId,
                Action = action,
                Actor = actor,
                Source = source,
                PreviousValue = Truncate(previousValue, 200),
                NewValue = Truncate(newValue, 200),
                Timestamp = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Conversation {ConversationId} {Action} by {Actor} via {Source}",
                conversationId, action, actor, source);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write audit entry for conversation {ConversationId}", conversationId);
        }
    }

    private static string? Truncate(string? value, int maxLength)
        => value is null ? null : value.Length <= maxLength ? value : value[..maxLength];

}
