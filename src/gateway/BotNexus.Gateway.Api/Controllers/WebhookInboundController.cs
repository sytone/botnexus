using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Webhooks;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Inbound webhook endpoint. External systems POST to
/// <c>/api/webhooks/{agentId}/{webhookId}</c> to deliver a message to an agent.
/// Every request must include a valid <c>X-BotNexus-Signature-256</c> header
/// computed as <c>sha256=HMAC-SHA256(rawBody, secret)</c>.
/// </summary>
[ApiController]
[Route("api/webhooks")]
public sealed class WebhookInboundController(
    IWebhookRegistrationStore registrationStore,
    IWebhookRunStore runStore,
    IInboundMessageOrchestrator orchestrator,
    IConversationDispatcher conversationDispatcher,
    IConversationStore conversationStore,
    ISessionStore sessionStore,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookInboundController> logger,
    WebhookInboundBodyGuard? bodyGuard = null,
    WebhookInboundQueue? inboundQueue = null,
    IHostApplicationLifetime? applicationLifetime = null) : ControllerBase
{
    private const string SignatureHeader = "X-BotNexus-Signature-256";
    private const int SyncTimeoutSeconds = 120;

    /// <summary>
    /// Fallback guard for callers that construct the controller without DI. The bounds are a
    /// security floor, not a tuning knob, so an unwired controller must still be bounded (#3807).
    /// </summary>
    private static readonly WebhookInboundBodyGuard DefaultBodyGuard = new();

    /// <summary>
    /// Fallback queue for callers that construct the controller without DI. Like the body guard,
    /// the bound is a correctness floor rather than a tuning knob (#3851): an unwired controller
    /// must still be bounded, because "unbounded" is the defect.
    /// </summary>
    private static readonly WebhookInboundQueue DefaultInboundQueue = new(new WebhookInboundQueueOptions());

    private WebhookInboundBodyGuard BodyGuard => bodyGuard ?? DefaultBodyGuard;

    private WebhookInboundQueue InboundQueue => inboundQueue ?? DefaultInboundQueue;

    /// <summary>
    /// Host shutdown signal folded into every dispatch. Before #3851 both background dispatch sites
    /// passed <c>CancellationToken.None</c>, so a shutting-down gateway could not stop an in-flight
    /// webhook turn or drain the ones queued behind it.
    /// </summary>
    private CancellationToken ShutdownToken =>
        applicationLifetime?.ApplicationStopping ?? CancellationToken.None;

    /// <summary>
    /// Accepts an inbound message from an external system, verifies the HMAC-SHA256
    /// signature, and routes the message to the target agent.
    /// </summary>
    /// <remarks>
    /// Response behaviour depends on <c>responseMode</c> in the request body:
    /// <list type="bullet">
    ///   <item><description><b>async</b> (default) — 202 Accepted with a <c>Location</c> poll URL. Agent runs in background.</description></item>
    ///   <item><description><b>sync</b> — holds the connection open (up to 120s) and returns the agent response inline.</description></item>
    ///   <item><description><b>callback</b> — 202 Accepted; POSTs result to <c>callbackUrl</c> when complete.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="agentId">Target agent ID from the URL.</param>
    /// <param name="webhookId">Webhook registration ID from the URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("{agentId}/{webhookId}")]
    [ProducesResponseType(typeof(WebhookAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(WebhookSyncResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Receive(
        string agentId,
        string webhookId,
        CancellationToken cancellationToken)
    {
        // ── 1. Read raw body for HMAC verification ───────────────────────────
        // This route is anonymous, and the signature is computed over these exact bytes, so the
        // read necessarily precedes authentication. That makes it an unauthenticated allocation
        // primitive unless it is bounded here (#3807): a declared-length check first (a truthful
        // oversized request then costs nothing), then an in-flight slot, then a length-capped copy.
        var guard = BodyGuard;

        if (guard.ExceedsDeclaredLength(Request.ContentLength))
        {
            logger.LogWarning(
                "Webhook '{WebhookId}' rejected — declared body length {Declared} exceeds the {Limit} byte ceiling, from {RemoteIp}.",
                webhookId, Request.ContentLength, guard.MaxBodyBytes, HttpContext.Connection.RemoteIpAddress);
            return StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new { error = $"Request body exceeds the {guard.MaxBodyBytes} byte limit." });
        }

        if (!guard.TryAcquireReadSlot())
        {
            logger.LogWarning(
                "Webhook '{WebhookId}' rejected — {Limit} concurrent pre-authentication body reads already in flight, from {RemoteIp}.",
                webhookId, guard.MaxInFlightReads, HttpContext.Connection.RemoteIpAddress);
            return StatusCode(
                StatusCodes.Status429TooManyRequests,
                new { error = "Too many concurrent webhook requests." });
        }

        byte[] rawBody;
        try
        {
            Request.EnableBuffering();
            var read = await guard.ReadBoundedAsync(Request.Body, cancellationToken);
            if (read.IsTooLarge)
            {
                logger.LogWarning(
                    "Webhook '{WebhookId}' rejected — body exceeds the {Limit} byte ceiling, from {RemoteIp}.",
                    webhookId, read.Limit, HttpContext.Connection.RemoteIpAddress);
                return StatusCode(
                    StatusCodes.Status413PayloadTooLarge,
                    new { error = $"Request body exceeds the {read.Limit} byte limit." });
            }

            rawBody = read.Body;
        }
        finally
        {
            // Released as soon as the pre-signature region ends. The cap bounds unauthenticated
            // reads, not the authenticated work that follows.
            guard.ReleaseReadSlot();
        }

        if (Request.Body.CanSeek)
            Request.Body.Position = 0;

        // ── 2. Parse request body ────────────────────────────────────────────
        WebhookInboundRequest? body;
        try
        {
            body = System.Text.Json.JsonSerializer.Deserialize<WebhookInboundRequest>(
                rawBody,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return BadRequest(new { error = "Invalid JSON body." });
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Message))
            return BadRequest(new { error = "message is required." });

        // ── 3. Load and validate registration ────────────────────────────────
        var typedWebhookId = WebhookId.From(webhookId);
        var registration = await registrationStore.GetAsync(typedWebhookId, cancellationToken);
        if (registration is null || !registration.Enabled)
            return NotFound(new { error = $"Webhook '{webhookId}' not found or disabled." });

        // ── 4. Verify HMAC signature ─────────────────────────────────────────
        var signatureHeader = Request.Headers[SignatureHeader].FirstOrDefault();
        if (!WebhookSecretHelper.VerifySignature(registration.Secret, rawBody, signatureHeader))
        {
            logger.LogWarning(
                "Webhook '{WebhookId}' rejected — invalid signature from {RemoteIp}.",
                webhookId, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Invalid signature." });
        }

        // ── 5. Resolve agent ─────────────────────────────────────────────────
        var typedAgentId = AgentId.From(agentId);

        // ── 6. Resolve or pin conversation ───────────────────────────────────
        ConversationId resolvedConversationId;
        if (registration.PinnedConversationId is { } pinned)
        {
            resolvedConversationId = pinned;
        }
        else
        {
            // Try to pin one — create a new conversation if not yet pinned.
            var now = DateTimeOffset.UtcNow;
            // Inbound-webhook origination (#2302): no human is in the loop, which is exactly
            // what read-only/composer gating needs to know. Minted through the single creation
            // seam (#2310) so provenance cannot be silently omitted.
            var conversation = BotNexus.Gateway.Abstractions.Models.ConversationFactory.CreateForWebhook(
                ConversationId.Create(),
                typedAgentId,
                title: $"Webhook: {registration.Label}",
                initiator: CitizenId.Of(typedAgentId),
                // #2121: stamp the owning registration id onto the conversation itself so the
                // portal (and source-specific retention) can attribute it from the summary rather
                // than from the metadata bag or the title text.
                sourceId: typedWebhookId.Value,
                timestamp: now);
            // Stamp authoritative webhook provenance so source-specific retention (#2125) can
            // identify this conversation by its originating registration id, never by title.
            WebhookConversationProvenance.Stamp(conversation.Metadata, typedWebhookId);
            var created = await conversationStore.CreateAsync(conversation, cancellationToken);
            var winner = await registrationStore.TryPinConversationAsync(
                typedWebhookId, created.ConversationId, cancellationToken);
            resolvedConversationId = winner ?? created.ConversationId;

            // Parallel first deliveries can both create candidates before either reaches the
            // compare-and-set. Keep only the winning conversation visible; the loser has not
            // been dispatched or bound yet, so it is safe to archive immediately.
            if (winner.HasValue && winner.Value != created.ConversationId)
                await conversationStore.ArchiveAsync(created.ConversationId, "webhook-loser-cleanup", System.Diagnostics.Activity.Current?.Id, "system", cancellationToken);
        }

        // ── 7. Resolve response mode ─────────────────────────────────────────
        var responseMode = body.ResponseMode ?? registration.DefaultResponseMode;

        // ── 8. Create run record ─────────────────────────────────────────────
        var run = new WebhookRun
        {
            Id = WebhookRunId.Create(),
            WebhookId = typedWebhookId,
            ConversationId = resolvedConversationId,
            Status = WebhookRunStatus.Pending,
            AcceptedAt = DateTimeOffset.UtcNow,
            AgentAction = body.AgentAction ?? true,
            CallbackUrl = body.CallbackUrl
        };
        run = await runStore.CreateAsync(run, cancellationToken);

        // Update only the usage timestamp. Re-saving the registration snapshot here can
        // erase the conversation pin established above because this request loaded the
        // snapshot before the compare-and-set mutation.
        await registrationStore.TouchLastUsedAsync(
            typedWebhookId,
            DateTimeOffset.UtcNow,
            cancellationToken);

        logger.LogInformation(
            "Webhook '{WebhookId}' accepted run '{RunId}' for agent '{AgentId}' (mode={Mode}, agentAction={AgentAction}).",
            webhookId, run.Id, agentId, responseMode, run.AgentAction);

        // ── 9. Dispatch ───────────────────────────────────────────────────────
        var pollUrl = $"{Request.Scheme}://{Request.Host}/api/webhooks/runs/{run.Id.Value}";

        if (!run.AgentAction)
        {
            // Store-only mode — append to the conversation's own session, but don't run the agent.
            var storedSessionId = await StoreMessageOnlyAsync(
                run, typedAgentId, resolvedConversationId, body.Message, cancellationToken);

            if (storedSessionId is null)
            {
                // The write did not land. Returning 202 here (issue #2839) handed the caller a
                // success receipt plus a valid-looking conversation id for a message that could
                // never be read back, so nothing signalled a retry. Fail loudly instead.
                run.Status = WebhookRunStatus.Failed;
                run.CompletedAt = DateTimeOffset.UtcNow;
                run.Error = $"Store-only delivery could not resolve a session for conversation '{resolvedConversationId.Value}'.";
                await runStore.UpdateAsync(run, cancellationToken);

                logger.LogError(
                    "Webhook '{WebhookId}' store-only run '{RunId}' could not resolve a session for conversation '{ConversationId}'.",
                    webhookId, run.Id, resolvedConversationId);

                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { error = "Could not resolve a session for the target conversation; message was not stored." });
            }

            run = run with
            {
                Status = WebhookRunStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                AgentResponse = null,
                SessionId = storedSessionId
            };
            await runStore.UpdateAsync(run, cancellationToken);
            return Accepted(new WebhookAcceptedResponse(run.Id.Value, pollUrl, resolvedConversationId.Value));
        }

        return responseMode switch
        {
            WebhookResponseMode.Sync => await HandleSyncAsync(run, typedAgentId, resolvedConversationId, body.Message, pollUrl, cancellationToken),
            WebhookResponseMode.Callback => await HandleCallbackAsync(run, typedAgentId, resolvedConversationId, body.Message, pollUrl, cancellationToken),
            _ => await HandleAsyncAsync(run, typedAgentId, resolvedConversationId, body.Message, pollUrl, cancellationToken)
        };
    }

    // ── Dispatch helpers ──────────────────────────────────────────────────────

    private async Task<IActionResult> HandleAsyncAsync(
        WebhookRun run, AgentId agentId, ConversationId conversationId,
        string message, string pollUrl, CancellationToken ct)
    {
        // Admission is decided HERE, on the request thread, before any 202 is written (#3851 AC4).
        // Deciding it inside the background task would mean the caller had already been handed a
        // success receipt by the time the refusal was known - which is the defect, not the fix.
        WebhookQueueTicket ticket;
        try
        {
            ticket = InboundQueue.Admit(agentId, conversationId);
        }
        catch (WebhookBackpressureException ex)
        {
            return await RejectAsync(run, agentId, ex);
        }

        if (!ticket.IsImmediate)
            await MarkQueuedAsync(run, agentId);

        var shutdown = ShutdownToken;
        var runTimeout = InboundQueue.RunTimeout;

        // Fire-and-forget in the sense that the CALLER does not wait - but the work itself is now
        // bounded, cancellable and observable, which the bare Task.Run it replaces was not.
        _ = Task.Run(async () =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            cts.CancelAfter(runTimeout);
            try
            {
                using var lease = await ticket.WaitAsync(cts.Token);
                await ExecuteAgentAsync(run, agentId, conversationId, message, cts.Token);
            }
            catch (WebhookNotDispatchedException ex)
            {
                await MarkTimedOutAsync(run, agentId, ex.Message);
            }
            catch (OperationCanceledException)
            {
                await MarkTimedOutAsync(
                    run, agentId,
                    $"Background webhook run exceeded its {runTimeout.TotalSeconds:0}s ceiling or the gateway is shutting down.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background webhook run '{RunId}' failed.", run.Id);
            }
        }, CancellationToken.None);

        Response.Headers["Location"] = pollUrl;
        return Accepted(new WebhookAcceptedResponse(run.Id.Value, pollUrl, conversationId.Value));
    }

    private async Task<IActionResult> HandleSyncAsync(
        WebhookRun run, AgentId agentId, ConversationId conversationId,
        string message, string pollUrl, CancellationToken ct)
    {
        WebhookQueueTicket ticket;
        try
        {
            ticket = InboundQueue.Admit(agentId, conversationId);
        }
        catch (WebhookBackpressureException ex)
        {
            return await RejectAsync(run, agentId, ex);
        }

        if (!ticket.IsImmediate)
            await MarkQueuedAsync(run, agentId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(SyncTimeoutSeconds));

        try
        {
            using (await ticket.WaitAsync(cts.Token))
            {
                await ExecuteAgentAsync(run, agentId, conversationId, message, cts.Token);
            }

            var completed = await runStore.GetAsync(run.Id, CancellationToken.None);
            if (completed?.Status == WebhookRunStatus.Completed)
                return Ok(new WebhookSyncResponse(completed.Id.Value, completed.AgentResponse, conversationId.Value));

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Agent run did not complete." });
        }
        catch (WebhookNotDispatchedException ex) when (!ct.IsCancellationRequested)
        {
            // The deadline expired while still queued: the agent never saw this message at all.
            // Say so, rather than reporting a timeout of work that never started.
            await MarkTimedOutAsync(run, agentId, ex.Message);
            Response.Headers["Location"] = pollUrl;
            return Accepted(new WebhookAcceptedResponse(run.Id.Value, pollUrl, conversationId.Value));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await MarkTimedOutAsync(run, agentId, $"Sync mode timeout after {SyncTimeoutSeconds}s.");
            Response.Headers["Location"] = pollUrl;
            return Accepted(new WebhookAcceptedResponse(run.Id.Value, pollUrl, conversationId.Value));
        }
    }

    private async Task<IActionResult> HandleCallbackAsync(
        WebhookRun run, AgentId agentId, ConversationId conversationId,
        string message, string pollUrl, CancellationToken ct)
    {
        var callbackUrl = run.CallbackUrl;

        WebhookQueueTicket ticket;
        try
        {
            ticket = InboundQueue.Admit(agentId, conversationId);
        }
        catch (WebhookBackpressureException ex)
        {
            return await RejectAsync(run, agentId, ex);
        }

        if (!ticket.IsImmediate)
            await MarkQueuedAsync(run, agentId);

        var shutdown = ShutdownToken;
        var runTimeout = InboundQueue.RunTimeout;

        _ = Task.Run(async () =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            cts.CancelAfter(runTimeout);
            try
            {
                using (await ticket.WaitAsync(cts.Token))
                {
                    await ExecuteAgentAsync(run, agentId, conversationId, message, cts.Token);
                }

                if (!string.IsNullOrWhiteSpace(callbackUrl))
                    await DeliverCallbackAsync(run.Id, callbackUrl, shutdown);
            }
            catch (WebhookNotDispatchedException ex)
            {
                await MarkTimedOutAsync(run, agentId, ex.Message);
            }
            catch (OperationCanceledException)
            {
                await MarkTimedOutAsync(
                    run, agentId,
                    $"Callback webhook run exceeded its {runTimeout.TotalSeconds:0}s ceiling or the gateway is shutting down.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Callback webhook run '{RunId}' failed.", run.Id);
            }
        }, CancellationToken.None);

        Response.Headers["Location"] = pollUrl;
        return Accepted(new WebhookAcceptedResponse(run.Id.Value, pollUrl, conversationId.Value));
    }

    /// <summary>
    /// Records a delivery refused because the agent's bounded queue was full, and returns the
    /// explicit <c>503</c> the caller needs in order to retry (#3851 AC4).
    /// </summary>
    private async Task<IActionResult> RejectAsync(
        WebhookRun run, AgentId agentId, WebhookBackpressureException ex)
    {
        run.Status = WebhookRunStatus.Rejected;
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.Error = ex.Message;
        await runStore.UpdateAsync(run, CancellationToken.None);

        logger.LogWarning(
            "Webhook run '{RunId}' rejected - agent '{AgentId}' inbound queue is full at depth {Depth}.",
            run.Id, agentId.Value, ex.MaxQueueDepth);

        return StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new { error = ex.Message, queueDepth = ex.MaxQueueDepth, runId = run.Id.Value });
    }

    /// <summary>
    /// Publishes <see cref="WebhookRunStatus.Queued"/> for a delivery that could not start
    /// immediately, and logs the agent's current backlog depth (#3851 AC2, AC5).
    /// </summary>
    /// <remarks>
    /// Called only when admission did NOT take the slot outright. A queued state set on every run
    /// would carry no more information than the <c>Running</c>-for-everything it replaces.
    /// </remarks>
    private async Task MarkQueuedAsync(WebhookRun run, AgentId agentId)
    {
        run.Status = WebhookRunStatus.Queued;
        await runStore.UpdateAsync(run, CancellationToken.None);

        logger.LogInformation(
            "Webhook run '{RunId}' queued for agent '{AgentId}' - {Waiting} delivery(s) waiting, bound {Depth}.",
            run.Id, agentId.Value, InboundQueue.WaitingCount(agentId), InboundQueue.MaxQueueDepth);
    }

    /// <summary>
    /// Records a run that hit its deadline, whether it timed out waiting for the slot or while
    /// executing (#3851 AC3).
    /// </summary>
    private async Task MarkTimedOutAsync(WebhookRun run, AgentId agentId, string error)
    {
        run.Status = WebhookRunStatus.Timeout;
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.Error = error;
        await runStore.UpdateAsync(run, CancellationToken.None);

        logger.LogWarning(
            "Webhook run '{RunId}' for agent '{AgentId}' timed out: {Error}", run.Id, agentId.Value, error);
    }

    private async Task ExecuteAgentAsync(
        WebhookRun run, AgentId agentId, ConversationId conversationId,
        string message, CancellationToken ct)
    {
        run.Status = WebhookRunStatus.Running;
        run.StartedAt = DateTimeOffset.UtcNow;
        await runStore.UpdateAsync(run, CancellationToken.None);

        try
        {
            // Build inbound message routing through the existing orchestrator —
            // same path as ConversationTool and all channel adapters.
            var inbound = new InboundMessage
            {
                ChannelType = ChannelKey.From("webhook"),
                SenderId = $"webhook:{run.WebhookId.Value}",
                Sender = CitizenId.Of(agentId),
                ChannelAddress = ChannelAddress.From(run.WebhookId.Value),
                Content = message.Trim(),
                RoutingHints = new InboundMessageRoutingHints(
                    RequestedAgentId: agentId,
                    RequestedSessionId: null,
                    RequestedConversationId: conversationId),
                Metadata = new Dictionary<string, object?>
                {
                    ["webhookRunId"] = run.Id.Value,
                    ["webhookId"] = run.WebhookId.Value
                }
            };

            var result = await orchestrator.AcceptAsync(inbound, ct);

            // Extract session ID and agent response from the session store.
            var sessionId = result.Dispatches.FirstOrDefault()?.Resolution.SessionId;
            string? agentResponse = null;
            if (sessionId.HasValue)
            {
                var session = await sessionStore.GetAsync(sessionId.Value, CancellationToken.None);
                agentResponse = session?.GetHistorySnapshot()
                    .LastOrDefault(e => e.Role == MessageRole.Assistant)
                    ?.Content;
            }

            run = run with
            {
                Status = WebhookRunStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                AgentResponse = agentResponse,
                SessionId = sessionId
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook run '{RunId}' agent execution failed.", run.Id);
            run.Status = WebhookRunStatus.Failed;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.Error = ex.Message;
        }
        finally
        {
            await runStore.UpdateAsync(run, CancellationToken.None);
        }
    }

    /// <summary>
    /// Store-only path: append the user message to the session bound to the requested conversation
    /// without executing an agent turn. Useful for audit trails and async aggregation.
    /// </summary>
    /// <remarks>
    /// Session resolution is delegated to <see cref="IConversationDispatcher"/> — the same seam
    /// <see cref="ExecuteAgentAsync"/> reaches through <see cref="IInboundMessageOrchestrator"/> —
    /// so the two modes cannot drift on <em>where</em> a message is stored. Before #2839 this method
    /// minted <c>SessionId.From(Guid.NewGuid())</c> and ignored <paramref name="conversationId"/>
    /// entirely, writing every delivery into a fresh session bound to no conversation.
    /// </remarks>
    /// <returns>
    /// The session the message was appended to, or <see langword="null"/> when no session could be
    /// resolved or the append was refused — the caller must then not report success.
    /// </returns>
    private async Task<SessionId?> StoreMessageOnlyAsync(
        WebhookRun run, AgentId agentId, ConversationId conversationId, string message, CancellationToken ct)
    {
        var inbound = new InboundMessage
        {
            ChannelType = ChannelKey.From("webhook"),
            SenderId = $"webhook:{run.WebhookId.Value}",
            Sender = CitizenId.Of(agentId),
            ChannelAddress = ChannelAddress.From(run.WebhookId.Value),
            Content = message.Trim(),
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: agentId,
                RequestedSessionId: null,
                RequestedConversationId: conversationId),
            Metadata = new Dictionary<string, object?>
            {
                ["webhookRunId"] = run.Id.Value,
                ["webhookId"] = run.WebhookId.Value
            }
        };

        var context = InboundMessageContext.FromInboundMessage(agentId, inbound);
        var dispatch = await conversationDispatcher.DispatchAsync(context, ct);
        var sessionId = dispatch.Resolution.SessionId;

        // Narrow append rather than a read-mutate-save of the whole aggregate (#2132): a store-only
        // write must not roll back a metadata patch or lifecycle transition that landed concurrently.
        var appended = await sessionStore.AppendEntriesAsync(
            sessionId,
            [new SessionEntry { Role = MessageRole.User, Content = message }],
            ct);

        return appended.Outcome == SessionMutationOutcome.Applied ? sessionId : null;
    }

    private async Task DeliverCallbackAsync(WebhookRunId runId, string callbackUrl, CancellationToken ct)
    {
        // Validate callback URL against SSRF before making any outbound request
        var validation = WebhookCallbackValidator.IsCallbackUrlSafe(callbackUrl);
        if (!validation.IsSafe)
        {
            logger.LogWarning(
                "Webhook run '{RunId}' callback to '{CallbackUrl}' blocked: {Reason}",
                runId, callbackUrl, validation.Reason);
            return;
        }

        var run = await runStore.GetAsync(runId, ct);
        if (run is null) return;

        try
        {
            using var http = httpClientFactory.CreateClient("WebhookCallback");
            http.Timeout = TimeSpan.FromSeconds(30);
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                runId = run.Id.Value,
                webhookId = run.WebhookId.Value,
                status = run.Status.ToString(),
                agentResponse = run.AgentResponse,
                conversationId = run.ConversationId.Value,
                completedAt = run.CompletedAt
            });
            await http.PostAsync(
                callbackUrl,
                new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
                ct);

            logger.LogInformation("Webhook run '{RunId}' callback delivered to '{CallbackUrl}'.", runId, callbackUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook run '{RunId}' callback delivery to '{CallbackUrl}' failed.", runId, callbackUrl);
        }
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

/// <summary>Request body for inbound webhook POST.</summary>
public sealed record WebhookInboundRequest
{
    /// <summary>The message to deliver to the agent.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Response mode override. Null uses the registration default (typically Async).
    /// </summary>
    public WebhookResponseMode? ResponseMode { get; init; }

    /// <summary>
    /// Whether the agent should process the message. Default true.
    /// Set to false to store the message without triggering an agent run.
    /// </summary>
    public bool? AgentAction { get; init; }

    /// <summary>
    /// URL to POST results to when <see cref="ResponseMode"/> is Callback.
    /// Ignored for other modes.
    /// </summary>
    public string? CallbackUrl { get; init; }
}

/// <summary>Response for async and callback modes (202 Accepted).</summary>
/// <param name="RunId">Webhook run identifier for polling.</param>
/// <param name="PollUrl">URL to GET for run status.</param>
/// <param name="ConversationId">Conversation the message was routed to.</param>
public sealed record WebhookAcceptedResponse(string RunId, string PollUrl, string ConversationId);

/// <summary>Response for sync mode (200 OK) when agent completes within timeout.</summary>
/// <param name="RunId">Webhook run identifier.</param>
/// <param name="AgentResponse">The agent's response text.</param>
/// <param name="ConversationId">Conversation the message was routed to.</param>
public sealed record WebhookSyncResponse(string RunId, string? AgentResponse, string ConversationId);
