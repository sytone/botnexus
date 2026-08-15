using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Audit;
using BotNexus.Domain.Primitives;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST endpoint for non-streaming chat. Routes through the gateway message queue
/// to ensure proper session serialization. For real-time streaming, use a
/// streaming channel extension (e.g. the SignalR channel) — the gateway itself
/// only exposes a REST surface; streaming transports live in channel extensions.
/// </summary>
/// <summary>
/// Represents chat controller.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ChatController : ControllerBase
{
    private readonly IAgentSupervisor _supervisor;
    private readonly ISessionStore _sessions;
    private readonly IToolAuditSink _toolAudit;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatController"/> class.
    /// </summary>
    /// <param name="supervisor">The agent supervisor for managing agent instances.</param>
    /// <param name="sessions">The session store for persisting conversation history.</param>
    /// <param name="toolAudit">
    /// The single execution-layer tool-audit sink (#2614 AC4). Optional so the many existing
    /// direct-construction call sites in tests keep compiling; it falls back to the same shared
    /// instance gateway composition registers, so the audit guarantee never depends on whether
    /// this controller happened to be resolved from DI.
    /// </param>
    public ChatController(IAgentSupervisor supervisor, ISessionStore sessions, IToolAuditSink? toolAudit = null)
    {
        _supervisor = supervisor;
        _sessions = sessions;
        _toolAudit = toolAudit ?? DefaultToolAuditSink.Instance;
    }

    /// <summary>
    /// Sends a message to an agent and returns the complete response.
    /// For streaming, connect via SignalR at <c>/hub/gateway</c>.
    /// </summary>
    /// <summary>
    /// Executes send.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The send result.</returns>
    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Send([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId))
            return BadRequest(new { error = "agentId is required." });

        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required." });

        try
        {
            var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
                ? Guid.NewGuid().ToString("N")
                : request.SessionId;

            var typedAgentId = AgentId.From(request.AgentId);
            var typedSessionId = SessionId.From(sessionId);

            // #2396: per-run model / thinking selection for a headless one-shot run. Stamped as
            // session metadata - the SAME seam the cron, soul and heartbeat triggers already use -
            // so the override travels through DefaultAgentSupervisor and the isolation strategy's
            // three-layer resolver rather than becoming a second, CLI-only resolution path.
            //
            // Deliberately skipped when neither override is supplied: the session must not be
            // created before the supervisor call on the ordinary path, because an unknown agent has
            // to 404 WITHOUT leaving a session row behind.
            if (!string.IsNullOrWhiteSpace(request.Model) || !string.IsNullOrWhiteSpace(request.Thinking))
            {
                var overrideSession = await _sessions.GetOrCreateAsync(typedSessionId, typedAgentId, CancellationToken.None);
                ApplyRunOverrides(overrideSession, request.Model, request.Thinking);
                await _sessions.SaveAsync(overrideSession, CancellationToken.None);
            }

            // Reject messages to sessions that are no longer accepting input.
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                var existingSession = await _sessions.GetAsync(typedSessionId, CancellationToken.None);
                if (existingSession is not null &&
                    (existingSession.Status == GatewaySessionStatus.Sealed ||
                     existingSession.Status == GatewaySessionStatus.Suspended))
                {
                    return Conflict(new { error = $"Session '{sessionId}' is {existingSession.Status} and cannot accept new messages." });
                }
            }

            // Use CancellationToken.None for agent work — client disconnect should not kill the agent
            var handle = await _supervisor.GetOrCreateAsync(typedAgentId, typedSessionId, CancellationToken.None);

            // If agent is already running, queue as follow-up instead of failing
            AgentResponse response;
            try
            {
                response = await handle.PromptAsync(request.Message, CancellationToken.None);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already running", StringComparison.OrdinalIgnoreCase))
            {
                await handle.FollowUpAsync(request.Message, CancellationToken.None);
                return Accepted(new ChatResponse(sessionId, "Message queued as follow-up — agent is currently processing a previous request.", null));
            }

            var session = await _sessions.GetOrCreateAsync(typedSessionId, typedAgentId, CancellationToken.None);
            session.SessionType = SessionType.UserAgent;
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = request.Message });
            // #2614 AC4: the REST chat path is a blocking PromptAsync boundary, so before this
            // slice it persisted final text ONLY - a run that shelled out, wrote files and then
            // summarised left no durable evidence the tools ran. Route it through the same sink
            // both other transports use, so the audit record no longer depends on transport choice.
            // Ordered before the assistant row, matching every other blocking call site.
            foreach (var toolEntry in _toolAudit.ProjectBlockingRun(_toolAudit.CaptureBlockingRun(response)))
                session.AddEntry(toolEntry);
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
            await _sessions.SaveAsync(session, CancellationToken.None);

            return Ok(new ChatResponse(
                sessionId,
                response.Content,
                response.Usage,
                [.. response.ToolCalls.Select(c => new ChatToolCall(c.ToolCallId, c.ToolName, c.IsError))]));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (AgentConcurrencyLimitExceededException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Records the per-run model / thinking selection on the session's metadata bag (#2396).
    ///
    /// <para>WHY METADATA AND NOT A NEW RESOLUTION PATH: the supervisor already reads
    /// <c>modelOverride</c> from session metadata, and the isolation strategy already resolves the
    /// thinking level through the three-layer <c>ModelOverrideResolver</c>. Introducing a CLI- or
    /// REST-specific override channel would create a second definition of "which model runs this
    /// turn", which is exactly the drift that makes an allow-list guard
    /// (<c>AgentDescriptor.AllowedModelIds</c>) bypassable by choosing a different entry point.
    /// Writing the same keys the triggers write means the existing guard applies unchanged.</para>
    ///
    /// <para>A blank value CLEARS the key rather than storing an empty string, so a follow-up run on
    /// the same session cannot inherit a stale override.</para>
    /// </summary>
    private static void ApplyRunOverrides(GatewaySession session, string? model, string? thinking)
    {
        if (string.IsNullOrWhiteSpace(model))
            session.Metadata.Remove("modelOverride");
        else
            session.Metadata["modelOverride"] = model.Trim();

        if (string.IsNullOrWhiteSpace(thinking))
            session.Metadata.Remove("thinkingOverride");
        else
            session.Metadata["thinkingOverride"] = thinking.Trim();
    }

    /// <summary>
    /// Injects a steering message into an active agent run.
    /// </summary>
    /// <summary>
    /// Executes steer.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The steer result.</returns>
    [HttpPost("steer")]
    public async Task<IActionResult> Steer([FromBody] AgentControlRequest request, CancellationToken cancellationToken)
    {
        var instance = _supervisor.GetInstance(AgentId.From(request.AgentId), SessionId.From(request.SessionId));
        if (instance is null)
            return NotFound(new { message = "Agent session not found." });

        try
        {
            var handle = await _supervisor.GetOrCreateAsync(AgentId.From(request.AgentId), SessionId.From(request.SessionId), cancellationToken);
            await handle.SteerAsync(request.Message, cancellationToken);
            return Accepted();
        }
        catch (AgentConcurrencyLimitExceededException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Queues a follow-up message for an active agent session.
    /// </summary>
    /// <summary>
    /// Executes follow up.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The follow up result.</returns>
    [HttpPost("follow-up")]
    public async Task<IActionResult> FollowUp([FromBody] AgentControlRequest request, CancellationToken cancellationToken)
    {
        var instance = _supervisor.GetInstance(AgentId.From(request.AgentId), SessionId.From(request.SessionId));
        if (instance is null)
            return NotFound(new { message = "Agent session not found." });

        try
        {
            var handle = await _supervisor.GetOrCreateAsync(AgentId.From(request.AgentId), SessionId.From(request.SessionId), cancellationToken);
            await handle.FollowUpAsync(request.Message, cancellationToken);
            return Accepted();
        }
        catch (AgentConcurrencyLimitExceededException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
    }
}

/// <summary>
/// Chat request payload.
/// </summary>
/// <param name="AgentId">The registered agent to run.</param>
/// <param name="Message">The prompt text.</param>
/// <param name="SessionId">Optional existing session to continue; a fresh one is minted when omitted.</param>
/// <param name="Model">
/// Optional per-run model override (<c>model-id</c> or <c>provider/model-id</c>), recorded as session
/// metadata and resolved by the existing override stack. <see langword="null"/> keeps the agent default.
/// </param>
/// <param name="Thinking">
/// Optional per-run thinking-level wire token (<c>minimal</c>…<c>max</c>), recorded as session metadata
/// and resolved by the existing override stack. <see langword="null"/> keeps the agent default.
/// </param>
public sealed record ChatRequest(
    string AgentId,
    string Message,
    string? SessionId = null,
    string? Model = null,
    string? Thinking = null);

/// <summary>Agent control request payload.</summary>
public sealed record AgentControlRequest(string AgentId, string SessionId, string Message);

/// <summary>
/// Chat response payload.
/// </summary>
/// <param name="SessionId">The session the turn ran on.</param>
/// <param name="Content">The agent's final text.</param>
/// <param name="Usage">Token usage for the turn, when the provider reported any.</param>
/// <param name="ToolCalls">
/// The tools the turn actually invoked (#2396). Present so a headless caller can tell an
/// answered-from-context turn apart from one that did work, without reading the transcript store.
/// Empty - never null - when the turn called no tools.
/// </param>
public sealed record ChatResponse(
    string SessionId,
    string Content,
    AgentResponseUsage? Usage = null,
    IReadOnlyList<ChatToolCall>? ToolCalls = null);

/// <summary>
/// One tool invocation summary on a <see cref="ChatResponse"/>. Intentionally narrower than
/// <see cref="AgentToolCallInfo"/>: arguments and result bodies are omitted because this contract is
/// consumed by shell pipelines, and echoing tool payloads back over the wire would widen the
/// disclosure surface of every REST chat call for no caller benefit.
/// </summary>
/// <param name="ToolCallId">The model's tool-use correlation id.</param>
/// <param name="ToolName">The tool that ran.</param>
/// <param name="IsError">True when the tool reported a failure.</param>
public sealed record ChatToolCall(string ToolCallId, string ToolName, bool IsError);
