using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using AgentId = BotNexus.Domain.Primitives.AgentId;
using SessionId = BotNexus.Domain.Primitives.SessionId;
using SessionType = BotNexus.Domain.Primitives.SessionType;
using BotNexus.Gateway.Api;
using BotNexus.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for session management — listing, inspecting, and deleting sessions.
/// </summary>
/// <summary>
/// Represents sessions controller.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class SessionsController : ControllerBase
{
    private readonly ISessionStore _sessions;
    private readonly ISubAgentManager _subAgentManager;
    private readonly ISecurityEventSink? _securityEvents;
    private readonly ILogger<SessionsController>? _logger;
    private readonly TranscriptExportOptions _transcriptExport;
    private readonly IAgentSupervisor? _supervisor;
    private readonly IMemoryStoreFactory? _memoryStoreFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionsController"/> class.
    /// </summary>
    /// <param name="sessions">The session store for managing conversation sessions.</param>
    /// <param name="subAgentManager">Sub-agent manager for session-scoped sub-agent lifecycle operations.</param>
    /// <param name="securityEvents">Trusted security-event sink for authorization denials, or null to disable emission.</param>
    /// <param name="logger">Optional logger for swallowed sink faults.</param>
    /// <param name="transcriptExport">Transcript export options controlling render-time secret redaction; when null, redaction defaults off.</param>
    /// <param name="supervisor">Agent supervisor used to read the live rendered system prompt off an active handle for the debug inspector; optional so tests without a running gateway still resolve the controller.</param>
    /// <param name="memoryStoreFactory">
    /// Resolves the per-agent memory store so a session delete also removes that session's indexed
    /// memory rows (#2956). Optional: memory is a configurable subsystem and the controller must
    /// resolve without it.
    /// </param>
    public SessionsController(
        ISessionStore sessions,
        ISubAgentManager? subAgentManager = null,
        ISecurityEventSink? securityEvents = null,
        ILogger<SessionsController>? logger = null,
        IOptions<TranscriptExportOptions>? transcriptExport = null,
        IAgentSupervisor? supervisor = null,
        IMemoryStoreFactory? memoryStoreFactory = null)
    {
        _transcriptExport = transcriptExport?.Value ?? new TranscriptExportOptions();
        _sessions = sessions;
        _subAgentManager = subAgentManager ?? NoOpSubAgentManager.Instance;
        _securityEvents = securityEvents;
        _logger = logger;
        _supervisor = supervisor;
        _memoryStoreFactory = memoryStoreFactory;
    }

    /// <summary>Lists sessions, optionally filtered by agent ID.</summary>
    /// <summary>
    /// Executes list.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="includeInactive">
    /// When <c>true</c>, includes sealed and expired sessions. When <c>false</c> (default),
    /// only active and suspended sessions are returned.
    /// </param>
    /// <param name="offset">Zero-based offset into the newest-first <b>filtered</b> session list (default 0).</param>
    /// <param name="limit">Maximum number of sessions to return (default 50, max 200).</param>
    /// <param name="conversationId">When set, only sessions linked to this conversation are returned (#2532 AC3).</param>
    /// <returns>The list result.</returns>
    [HttpGet]
    public async Task<ActionResult> List(
        [FromQuery] string? agentId,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        // #2411: this collection endpoint scales with session count, the one axis that grows
        // without bound on a long-lived gateway. Validate and clamp exactly as the
        // {sessionId}/history and {sessionId}/debug endpoints in this file already do.
        if (offset < 0)
            return BadRequest(new { error = "offset must be greater than or equal to zero." });

        if (limit <= 0)
            return BadRequest(new { error = "limit must be greater than zero." });

        var boundedLimit = Math.Min(limit, 200);

        string? normalizedAgentId = null;
        if (!string.IsNullOrWhiteSpace(agentId))
            normalizedAgentId = AgentId.From(agentId).Value;

        SessionSummaryPage page;
        try
        {
            // #2532: the agent and status filters are part of the STORE query, not applied to the
            // page after it comes back. Filtering a page post-hoc put limit/offset in a different
            // coordinate space from the rows the client received: a client advancing offset by the
            // number of rows it got crept forward one row at a time and only terminated by walking
            // the entire global session table (120+ requests observed in the wild). With the
            // predicate in the store, offset addresses exactly the set the client is consuming.
            //
            // DateTimeOffset.MinValue keeps the historical "no time window" contract.
            page = await _sessions.ListSummaryPageAsync(
                new SessionSummaryQuery(
                    DateTimeOffset.MinValue,
                    normalizedAgentId,
                    string.IsNullOrWhiteSpace(conversationId) ? null : conversationId,
                    includeInactive,
                    boundedLimit,
                    offset),
                cancellationToken);
        }
        catch (SessionStoreUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Session store temporarily unavailable. Please retry." });
        }

        var items = page.Items.Select(s => new
        {
            sessionId = s.SessionId,
            agentId = s.AgentId,
            channelType = s.ChannelType?.Value,
            // Phase 9 / P9-B-2 (#627): conversationId is already projected to null for the
            // unset sentinel by SessionSummary, so portal/REST clients see a stable shape.
            conversationId = s.ConversationId,
            status = s.Status.ToString(),
            sessionType = s.SessionType.Value,
            isInteractive = s.IsInteractive,
            messageCount = s.MessageCount,
            createdAt = s.CreatedAt,
            updatedAt = s.UpdatedAt
        }).ToList();

        // #2532 AC5: total/hasMore are the authoritative termination signal. Clients must not
        // infer exhaustion from a short page - the server clamps limit to its own maximum, so
        // "returned < requested" says nothing about whether more rows exist (#2499).
        return Ok(new
        {
            sessions = items,
            totalCount = page.TotalCount,
            hasMore = page.HasMore,
            offset,
            limit = boundedLimit
        });
    }

    /// <summary>
    /// Gets aggregate session statistics.
    /// </summary>
    /// <param name="agentId">Optional agent ID filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("stats")]
    public async Task<ActionResult> GetStats(
        [FromQuery] string? agentId,
        CancellationToken cancellationToken = default)
    {
        AgentId? parsedAgentId = null;
        if (!string.IsNullOrWhiteSpace(agentId))
            parsedAgentId = AgentId.From(agentId);

        var stats = await _sessions.GetStatsAsync(parsedAgentId, cancellationToken);
        if (stats is null)
            return NotFound("Session statistics not supported by the current store implementation.");

        return Ok(stats);
    }

    /// <summary>Gets a specific session by ID.</summary>
    /// <summary>
    /// Executes get.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The get result.</returns>
    [HttpGet("{sessionId}")]
    public async Task<ActionResult<GatewaySession>> Get(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
        return session is not null ? Ok(session) : NotFound();
    }

    /// <summary>Lists sub-agents for a specific session.</summary>
    /// <summary>
    /// Executes list sub agents.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list sub agents result.</returns>
    [HttpGet("{sessionId}/subagents")]
    [ProducesResponseType(typeof(IReadOnlyList<SubAgentInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<SubAgentInfo>>> ListSubAgents(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
        if (session is null)
            return NotFound();

        var subAgents = await _subAgentManager.ListAsync(SessionId.From(sessionId), cancellationToken);
        return Ok(subAgents);
    }

    /// <summary>
    /// Returns historical sub-agent session rows for the given parent session from
    /// the <c>sub_agent_sessions</c> store, ordered by <c>started_at</c> ascending.
    /// Unlike <c>GET /subagents</c> (which returns live runtime state), this endpoint
    /// returns persisted history including completed and failed runs.
    /// </summary>
    /// <param name="sessionId">The parent session ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>List of sub-agent session summaries.</returns>
    [HttpGet("{sessionId}/subagents/history")]
    [ProducesResponseType(typeof(IReadOnlyList<SubAgentSessionSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<SubAgentSessionSummary>>> GetSubAgentHistory(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
        if (session is null)
            return NotFound();

        var history = await _sessions.ListSubAgentSessionsAsync(SessionId.From(sessionId), cancellationToken);
        return Ok(history);
    }

    /// <summary>Kills a sub-agent owned by the specified session.</summary>
    /// <summary>
    /// Executes kill sub agent.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="subAgentId">The sub agent id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The kill sub agent result.</returns>
    [HttpDelete("{sessionId}/subagents/{subAgentId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> KillSubAgent(string sessionId, string subAgentId, CancellationToken cancellationToken)
    {
        var typedSessionId = SessionId.From(sessionId);
        var session = await _sessions.GetAsync(typedSessionId, cancellationToken);
        if (session is null)
            return NotFound();

        // Per-session caller authorization (#558). Killing a sub-agent is a state
        // mutation, so it must enforce the same caller-identity gate as Delete and
        // Suspend - otherwise any caller able to name a valid (sessionId, subAgentId)
        // pair could terminate a sub-agent run owned by a different caller.
        var authorizationFailure = AuthorizeSessionCaller(session);
        if (authorizationFailure is not null)
            return authorizationFailure;

        var subAgent = await _subAgentManager.GetAsync(subAgentId, cancellationToken);
        if (subAgent is null)
            return NotFound();

        if (subAgent.ParentSessionId != typedSessionId)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sub-agent does not belong to the requested session." });

        var killed = await _subAgentManager.KillAsync(subAgentId, typedSessionId, cancellationToken);
        if (!killed)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Gets paginated session history for long-running conversations.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="offset">The zero-based history offset.</param>
    /// <param name="limit">The maximum number of entries to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The paginated session history.</returns>
    [HttpGet("{sessionId}/history")]
    public async Task<ActionResult<SessionHistoryResponse>> GetHistory(
        string sessionId,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
            return BadRequest(new { error = "offset must be greater than or equal to zero." });

        if (limit <= 0)
            return BadRequest(new { error = "limit must be greater than zero." });

        var boundedLimit = Math.Min(limit, 200);

        var session = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
        if (session is null)
            return NotFound();

        var totalCount = session.History.Count;
        var entries = session.GetHistorySnapshot(offset, boundedLimit);
        return Ok(new SessionHistoryResponse(offset, boundedLimit, totalCount, entries));
    }

    /// <summary>
    /// Exports the session history as a markdown transcript document.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Markdown transcript as a downloadable file.</returns>
    [HttpGet("{sessionId}/export/markdown")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ExportMarkdown(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
        if (session is null)
            return NotFound();

        var markdown = BotNexus.Gateway.Sessions.SessionTranscriptRenderer.RenderMarkdown(session, session.AgentId.Value, _transcriptExport.RedactSecrets);
        if (markdown is null)
            return NoContent();

        var bytes = System.Text.Encoding.UTF8.GetBytes(markdown);
        return File(bytes, "text/markdown", $"session-{sessionId}.md");
    }

    /// <summary>
    /// Gets debug information for a specific session: system prompt, paginated history, and metadata.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="offset">Zero-based history offset (default 0).</param>
    /// <param name="limit">Maximum number of history entries to return (default 50, max 200).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Debug snapshot: session fields, system prompt, history, metadata.</returns>
    [HttpGet("{sessionId}/debug")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> GetDebug(
        string sessionId,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
        if (session is null)
            return NotFound();

        var boundedLimit = Math.Min(Math.Max(limit, 1), 200);
        var boundedOffset = Math.Max(offset, 0);

        var totalCount = session.History.Count;
        var entries = session.GetHistorySnapshot(boundedOffset, boundedLimit);

        // Resolve the system prompt with the live handle as the source of truth. An active
        // InProcessAgentHandle re-renders the prompt every turn and exposes it via
        // GetContextDiagnostics().SystemPrompt, so reading it here reflects exactly what the
        // model sees right now — and survives the fact that the stamped field is in-memory only.
        // Fall back to GatewaySession.LastRenderedSystemPrompt (stamped at handle creation) for
        // cold/inactive sessions whose handle is no longer resident (e.g. after an idle eviction).
        string? systemPrompt = null;
        DateTimeOffset? systemPromptCapturedAt = null;

        var handle = _supervisor?.GetHandle(session.AgentId, session.SessionId);
        if (handle is IAgentHandleInspector inspector
            && inspector.GetContextDiagnostics() is { SystemPrompt: { Length: > 0 } livePrompt })
        {
            systemPrompt = livePrompt;
            systemPromptCapturedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            systemPrompt = session.LastRenderedSystemPrompt;
            systemPromptCapturedAt = session.LastRenderedSystemPromptAt;
        }

        var isExecutionLive = handle?.IsRunning == true;
        var isCronSession = session.ChannelType?.Value.Equals("cron", StringComparison.Ordinal) == true;
        var lifecycleDiagnostic = isCronSession && session.Status == SessionStatus.Active
            ? isExecutionLive ? "live-execution" : "stale-persisted-active"
            : null;

        var result = new
        {
            sessionId = session.SessionId.Value,
            agentId = session.AgentId.Value,
            status = session.Status.ToString(),
            sessionType = session.SessionType.Value,
            createdAt = session.CreatedAt,
            updatedAt = session.UpdatedAt,
            messageCount = session.MessageCount,
            conversationId = session.ConversationId.IsInitialized() ? session.ConversationId.Value : (string?)null,
            channelType = session.ChannelType?.Value,
            isExecutionLive,
            lifecycleDiagnostic,
            systemPrompt = systemPrompt,
            systemPromptCapturedAt = systemPromptCapturedAt,
            history = new
            {
                totalCount,
                offset = boundedOffset,
                limit = boundedLimit,
                entries
            },
            metadata = session.Metadata
        };

        return Ok(result);
    }

    /// <summary>
    /// Gets metadata for a specific session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session metadata key-value pairs.</returns>
    [HttpGet("{sessionId}/metadata")]
    [ProducesResponseType(typeof(Dictionary<string, object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Dictionary<string, object?>>> GetMetadata(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
        if (session is null)
            return NotFound();

        var authorizationFailure = AuthorizeSessionCaller(session);
        if (authorizationFailure is not null)
            return authorizationFailure;

        return Ok(session.Metadata);
    }

    /// <summary>
    /// Merges metadata entries into a specific session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="metadataPatch">JSON object containing metadata keys to add/update. Keys with null values are removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated session metadata key-value pairs.</returns>
    [HttpPatch("{sessionId}/metadata")]
    [ProducesResponseType(typeof(Dictionary<string, object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Dictionary<string, object?>>> PatchMetadata(
        string sessionId,
        [FromBody] JsonElement metadataPatch,
        CancellationToken cancellationToken)
    {
        if (metadataPatch.ValueKind != JsonValueKind.Object)
            return BadRequest(new { error = "Metadata patch body must be a JSON object." });

        var session = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
        if (session is null)
            return NotFound();

        var authorizationFailure = AuthorizeSessionCaller(session);
        if (authorizationFailure is not null)
            return authorizationFailure;

        var patch = new Dictionary<string, object?>();
        foreach (var property in metadataPatch.EnumerateObject())
        {
            // A JSON null means "remove this key"; the store's merge honours that convention.
            patch[property.Name] = property.Value.ValueKind == JsonValueKind.Null
                ? null
                : ConvertJsonElement(property.Value);
        }

        // #2132: patch through the narrow atomic mutation instead of read-mutate-SaveAsync. The
        // whole-aggregate save would rewrite the transcript from the snapshot read above, silently
        // discarding any turn the agent appended while this request was in flight.
        var result = await _sessions.PatchMetadataAsync(SessionId.From(sessionId), patch, cancellationToken);
        return result.Outcome switch
        {
            SessionMutationOutcome.NotFound => NotFound(),
            SessionMutationOutcome.Conflict => Conflict(new { error = "Session metadata could not be updated because the session changed concurrently." }),
            _ => Ok(result.Metadata)
        };
    }

    private ObjectResult? AuthorizeSessionCaller(GatewaySession session)
    {
        var items = HttpContext?.Items;
        if (items is not null &&
            items.TryGetValue(GatewayAuthMiddleware.CallerIdentityItemKey, out var identityValue) &&
            identityValue is GatewayCallerIdentity identity &&
            !string.IsNullOrWhiteSpace(identity.CallerId) &&
            !string.Equals(identity.CallerId, session.CallerId, StringComparison.Ordinal))
        {
            // #1646: a per-session scope-check denial is an authorization boundary event. Emit one
            // SecurityEvent to the trusted sink (hashed caller, session target); best-effort so the
            // 403 decision is never blocked or altered by observability.
            EmitAuthorizationDenied(identity.CallerId, session.SessionId.Value);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Caller is not authorized for this session." });
        }

        return null;
    }

    /// <summary>
    /// Emits one authorization-denied security event to the trusted sink. The actor id is a salted
    /// hash of the caller id so the record never carries the raw identity. Best-effort: a null sink
    /// is a no-op and any sink fault is swallowed/logged so the denial decision is never altered.
    /// </summary>
    private void EmitAuthorizationDenied(string callerId, string sessionReference)
    {
        if (_securityEvents is null)
            return;

        try
        {
            var evt = new SecurityEvent(
                SecurityEventCategory.Authorization,
                "gateway.session.access.denied",
                SecurityEventOutcome.Denied,
                SecurityEventSeverity.Medium,
                Actor: new SecurityEventActor(SecurityActorKind.Operator, ActorPseudonym.For(callerId)),
                Target: new SecurityEventTarget(SecurityTargetKind.Session, sessionReference),
                Policy: SecurityPolicyDecision.Deny,
                Control: SecurityControlFamily.Authorization);
            _securityEvents.Record(evt);
        }
        catch (Exception ex)
        {
            // Observability must never break the authorization path; swallow and log.
            _logger?.LogWarning(ex, "Failed to record session authorization-denied security event.");
        }
    }


    /// <summary>Deletes a session.</summary>
    /// <summary>
    /// Executes delete.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The delete result.</returns>
    [HttpDelete("{sessionId}")]
    public async Task<ActionResult> Delete(string sessionId, CancellationToken cancellationToken)
    {
        var sid = SessionId.From(sessionId);

        // Per-session caller authorization (#558). Load before deleting so we can
        // verify the caller owns this session. If the session is already gone,
        // preserve the idempotent NoContent semantic the wire has always
        // returned — surfacing 404 here would create an existence-disclosure
        // oracle to authenticated probes and break DELETE retry idempotency.
        var session = await _sessions.GetAsync(sid, cancellationToken);
        if (session is null)
            return NoContent();

        var authorizationFailure = AuthorizeSessionCaller(session);
        if (authorizationFailure is not null)
            return authorizationFailure;

        await _sessions.DeleteAsync(sid, cancellationToken);

        // #2956: the session row and its indexed memory rows are two stores that only ever
        // diverged in one direction - memory indexing had an insert path and no delete path - so
        // deleting a session used to be cosmetic and its content stayed searchable forever.
        // Prune here, in the same operation, and only after authorization has passed.
        await PruneSessionMemoryAsync(session.AgentId.Value, sid.Value, cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Removes the deleted session's indexed memory rows (#2956).
    /// </summary>
    /// <remarks>
    /// Best-effort by design: the session row is already gone by the time this runs, so failing the
    /// request would report a delete that did in fact happen and would make the endpoint
    /// non-idempotent on retry for a fault the caller cannot act on. The divergence is logged
    /// instead, and the startup reconciliation pass converges it on the next restart.
    /// </remarks>
    private async Task PruneSessionMemoryAsync(string agentId, string sessionId, CancellationToken cancellationToken)
    {
        if (_memoryStoreFactory is null)
            return;

        try
        {
            // #2608: a reaped sub-agent workspace has no store; opening it is unrecoverable.
            if (!_memoryStoreFactory.StoreLocationExists(AgentId.From(agentId)))
                return;

            var store = _memoryStoreFactory.Create(AgentId.From(agentId));
            var pruned = await store.DeleteBySessionAsync(sessionId, cancellationToken);
            if (pruned > 0)
            {
                _logger?.LogInformation(
                    "Pruned {PrunedRows} memory row(s) for deleted session '{SessionId}'.",
                    pruned,
                    sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Failed to prune memory rows for deleted session '{SessionId}'; startup reconciliation will converge them.",
                sessionId);
        }
    }

    /// <summary>Suspends an active session.</summary>
    /// <summary>
    /// Executes suspend.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The suspend result.</returns>
    [HttpPatch("{sessionId}/suspend")]
    public async Task<ActionResult<GatewaySession>> Suspend(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
        if (session is null)
            return NotFound();

        // Per-session caller authorization (#558).
        var authorizationFailure = AuthorizeSessionCaller(session);
        if (authorizationFailure is not null)
            return authorizationFailure;

        if (session.Status != SessionStatus.Active)
            return Conflict(new { error = $"Cannot suspend session in '{session.Status}' state." });

        // #2132: compare-and-set on the persisted status. The pre-check above is a fast, friendly
        // rejection; this is the authoritative one, and it writes only the status column so a turn
        // appended concurrently is not rolled back by a whole-aggregate save.
        var transition = await _sessions.TransitionStatusAsync(
            SessionId.From(sessionId),
            [SessionStatus.Active],
            SessionStatus.Suspended,
            cancellationToken);

        return transition.Outcome switch
        {
            SessionMutationOutcome.NotFound => NotFound(),
            SessionMutationOutcome.Conflict => Conflict(new { error = $"Cannot suspend session in '{transition.Status}' state." }),
            _ => Ok(await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken))
        };
    }

    /// <summary>Resumes a suspended session.</summary>
    /// <summary>
    /// Executes resume.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resume result.</returns>
    [HttpPatch("{sessionId}/resume")]
    public async Task<ActionResult<GatewaySession>> Resume(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
        if (session is null)
            return NotFound();

        // Per-session caller authorization (#558).
        var authorizationFailure = AuthorizeSessionCaller(session);
        if (authorizationFailure is not null)
            return authorizationFailure;

        if (session.Status != SessionStatus.Suspended)
            return Conflict(new { error = $"Cannot resume session in '{session.Status}' state." });

        // #2132: see Suspend - the authoritative check is the store-level compare-and-set.
        var transition = await _sessions.TransitionStatusAsync(
            SessionId.From(sessionId),
            [SessionStatus.Suspended],
            SessionStatus.Active,
            cancellationToken);

        return transition.Outcome switch
        {
            SessionMutationOutcome.NotFound => NotFound(),
            SessionMutationOutcome.Conflict => Conflict(new { error = $"Cannot resume session in '{transition.Status}' state." }),
            _ => Ok(await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken))
        };
    }

    /// <summary>Seals a completed sub-agent session to prevent reuse.</summary>
    /// <summary>
    /// Executes seal.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The seal result.</returns>
    [HttpPatch("{sessionId}/seal")]
    public async Task<ActionResult> Seal(string sessionId, CancellationToken cancellationToken)
    {
        var sid = SessionId.From(sessionId);
        var session = await _sessions.GetAsync(sid, cancellationToken);
        if (session is null)
            return NotFound();

        // Per-session caller authorization (mirrors GetMetadata / PatchMetadata).
        // Without this guard, any authenticated caller that knows a sub-agent
        // sessionId could seal someone else's session — a control-plane integrity
        // and DoS risk surfaced by the #555 bug-hunt critique.
        var authorizationFailure = AuthorizeSessionCaller(session);
        if (authorizationFailure is not null)
            return authorizationFailure;

        // Phase 5 / F-6 step 2b (#555): sub-agent eligibility is driven by the
        // typed SessionType discriminator (persisted on the session row by
        // SqliteSessionStore / FileSessionStore; back-filled for legacy rows by
        // SessionStoreBase.InferSessionType) rather than the legacy
        // SessionId.IsSubAgent substring check. This is the last runtime call
        // site to migrate — pinned by AgentKindArchitectureTests.
        if (!session.SessionType.Equals(SessionType.AgentSubAgent))
            return BadRequest(new { error = "Only sub-agent sessions can be sealed" });

        if (session.Status == SessionStatus.Active || session.Status == SessionStatus.Suspended)
            return Conflict(new { error = "Cannot seal an active session" });

        if (session.Status == SessionStatus.Sealed)
            return NoContent();

        // #2132: seal via compare-and-set so a concurrent transcript append is not rolled back and
        // a competing seal/reset is reported rather than overwritten.
        var transition = await _sessions.TransitionStatusAsync(
            sid,
            [SessionStatus.Expired],
            SessionStatus.Sealed,
            cancellationToken);

        return transition.Outcome switch
        {
            SessionMutationOutcome.NotFound => NotFound(),
            SessionMutationOutcome.Conflict when transition.Status == SessionStatus.Sealed => NoContent(),
            SessionMutationOutcome.Conflict => Conflict(new { error = $"Cannot seal session in '{transition.Status}' state." }),
            _ => Ok(new { sessionId = sid.Value, status = "Sealed", updatedAt = transition.UpdatedAt })
        };
    }

    private static object? ConvertJsonElement(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => ConvertJsonElement(property.Value)),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElement)
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var intValue) => intValue,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.Null => null,
            _ => null
        };

    private sealed class NoOpSubAgentManager : ISubAgentManager
    {
        public static readonly NoOpSubAgentManager Instance = new();

        /// <summary>
        /// Executes spawn async.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="ct">The ct.</param>
        /// <returns>The spawn async result.</returns>
        public Task<SubAgentInfo> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Sub-agent spawning is not supported by this controller instance.");

        /// <summary>
        /// Executes list async.
        /// </summary>
        /// <param name="parentSessionId">The parent session id.</param>
        /// <param name="ct">The ct.</param>
        /// <returns>The list async result.</returns>
        public Task<IReadOnlyList<SubAgentInfo>> ListAsync(SessionId parentSessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SubAgentInfo>>([]);

        /// <summary>
        /// Executes get async.
        /// </summary>
        /// <param name="subAgentId">The sub agent id.</param>
        /// <param name="ct">The ct.</param>
        /// <returns>The get async result.</returns>
        public Task<SubAgentInfo?> GetAsync(string subAgentId, CancellationToken ct = default)
            => Task.FromResult<SubAgentInfo?>(null);

        /// <summary>
        /// Executes kill async.
        /// </summary>
        /// <param name="subAgentId">The sub agent id.</param>
        /// <param name="requestingSessionId">The requesting session id.</param>
        /// <param name="ct">The ct.</param>
        /// <returns>The kill async result.</returns>
        public Task<bool> KillAsync(string subAgentId, SessionId requestingSessionId, CancellationToken ct = default)
            => Task.FromResult(false);

        /// <summary>
        /// Executes on completed async.
        /// </summary>
        /// <param name="subAgentId">The sub agent id.</param>
        /// <param name="resultSummary">The result summary.</param>
        /// <param name="ct">The ct.</param>
        /// <returns>The on completed async result.</returns>
        public Task OnCompletedAsync(string subAgentId, string resultSummary, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
