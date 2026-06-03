using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Webhooks;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for webhook registration management.
/// Allows external systems to register inbound HTTP endpoints that route messages
/// to a specific agent and optionally pin to a conversation.
/// </summary>
[ApiController]
[Route("api/webhooks")]
public sealed class WebhooksController(
    IWebhookRegistrationStore registrationStore,
    IWebhookRunStore runStore,
    ILogger<WebhooksController> logger) : ControllerBase
{
    // ── Registration CRUD ────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new webhook registration. Returns the plaintext secret once — it is
    /// not stored and cannot be retrieved again.
    /// </summary>
    /// <param name="request">Registration details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("registrations")]
    [ProducesResponseType(typeof(WebhookRegistrationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WebhookRegistrationResponse>> Create(
        [FromBody] CreateWebhookRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId))
            return BadRequest(new { error = "agentId is required." });

        if (string.IsNullOrWhiteSpace(request.Label))
            return BadRequest(new { error = "label is required." });

        var secret = WebhookSecretHelper.GenerateSecret();
        var registration = new WebhookRegistration
        {
            Id = WebhookId.Create(),
            Label = request.Label,
            AgentId = AgentId.From(request.AgentId),
            PinnedConversationId = string.IsNullOrWhiteSpace(request.ConversationId)
                ? null
                : ConversationId.From(request.ConversationId),
            Secret = secret,
            DefaultResponseMode = request.DefaultResponseMode ?? WebhookResponseMode.Async,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var created = await registrationStore.CreateAsync(registration, cancellationToken);

        logger.LogInformation(
            "Created webhook registration '{WebhookId}' for agent '{AgentId}' (label={Label}).",
            created.Id, created.AgentId, created.Label);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var inboundUrl = $"{baseUrl}/api/webhooks/{created.AgentId.Value}/{created.Id.Value}";

        return CreatedAtAction(
            nameof(Get),
            new { webhookId = created.Id.Value },
            new WebhookRegistrationResponse(created, inboundUrl, secret));
    }

    /// <summary>
    /// Gets a webhook registration by ID. Does not return the secret.
    /// </summary>
    /// <param name="webhookId">The webhook registration ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("registrations/{webhookId}")]
    [ProducesResponseType(typeof(WebhookRegistrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WebhookRegistrationResponse>> Get(
        string webhookId,
        CancellationToken cancellationToken)
    {
        var registration = await registrationStore.GetAsync(WebhookId.From(webhookId), cancellationToken);
        if (registration is null)
            return NotFound(new { error = $"Webhook registration '{webhookId}' not found." });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var inboundUrl = $"{baseUrl}/api/webhooks/{registration.AgentId.Value}/{registration.Id.Value}";

        return Ok(new WebhookRegistrationResponse(registration, inboundUrl, secret: null));
    }

    /// <summary>
    /// Lists all webhook registrations, optionally filtered by agent.
    /// Secrets are never returned in list responses.
    /// </summary>
    /// <param name="agentId">Optional agent ID filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("registrations")]
    [ProducesResponseType(typeof(IReadOnlyList<WebhookRegistrationResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WebhookRegistrationResponse>>> List(
        [FromQuery] string? agentId,
        CancellationToken cancellationToken)
    {
        var filter = string.IsNullOrWhiteSpace(agentId) ? null : (AgentId?)AgentId.From(agentId);
        var registrations = await registrationStore.ListAsync(filter, cancellationToken);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var responses = registrations
            .Select(r => new WebhookRegistrationResponse(
                r,
                $"{baseUrl}/api/webhooks/{r.AgentId.Value}/{r.Id.Value}",
                secret: null))
            .ToList();

        return Ok(responses);
    }

    /// <summary>
    /// Updates the label, enabled state, or default response mode of a registration.
    /// The secret and agentId are immutable after creation.
    /// </summary>
    /// <param name="webhookId">The webhook registration ID.</param>
    /// <param name="request">Fields to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPut("registrations/{webhookId}")]
    [ProducesResponseType(typeof(WebhookRegistrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WebhookRegistrationResponse>> Update(
        string webhookId,
        [FromBody] UpdateWebhookRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await registrationStore.GetAsync(WebhookId.From(webhookId), cancellationToken);
        if (existing is null)
            return NotFound(new { error = $"Webhook registration '{webhookId}' not found." });

        var updated = existing with
        {
            Label = string.IsNullOrWhiteSpace(request.Label) ? existing.Label : request.Label,
            Enabled = request.Enabled ?? existing.Enabled,
            DefaultResponseMode = request.DefaultResponseMode ?? existing.DefaultResponseMode
        };

        var saved = await registrationStore.UpdateAsync(updated, cancellationToken);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var inboundUrl = $"{baseUrl}/api/webhooks/{saved.AgentId.Value}/{saved.Id.Value}";

        return Ok(new WebhookRegistrationResponse(saved, inboundUrl, secret: null));
    }

    /// <summary>
    /// Deletes a webhook registration. Any pending runs will complete but no new
    /// inbound POSTs will be accepted after deletion.
    /// </summary>
    /// <param name="webhookId">The webhook registration ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpDelete("registrations/{webhookId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string webhookId, CancellationToken cancellationToken)
    {
        var existing = await registrationStore.GetAsync(WebhookId.From(webhookId), cancellationToken);
        if (existing is null)
            return NotFound(new { error = $"Webhook registration '{webhookId}' not found." });

        await registrationStore.DeleteAsync(WebhookId.From(webhookId), cancellationToken);

        logger.LogInformation("Deleted webhook registration '{WebhookId}'.", webhookId);
        return NoContent();
    }

    // ── Run status polling ───────────────────────────────────────────────────

    /// <summary>
    /// Gets the status of a webhook run. Used to poll for completion after a 202 response.
    /// </summary>
    /// <param name="runId">The webhook run ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("runs/{runId}")]
    [ProducesResponseType(typeof(WebhookRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WebhookRunResponse>> GetRun(
        string runId,
        CancellationToken cancellationToken)
    {
        var run = await runStore.GetAsync(WebhookRunId.From(runId), cancellationToken);
        if (run is null)
            return NotFound(new { error = $"Webhook run '{runId}' not found." });

        return Ok(new WebhookRunResponse(run));
    }

    /// <summary>
    /// Lists recent runs for a webhook registration.
    /// </summary>
    /// <param name="webhookId">The webhook registration ID.</param>
    /// <param name="limit">Maximum number of runs to return (1–100, default 20).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("registrations/{webhookId}/runs")]
    [ProducesResponseType(typeof(IReadOnlyList<WebhookRunResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WebhookRunResponse>>> ListRuns(
        string webhookId,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var runs = await runStore.ListByWebhookAsync(
            WebhookId.From(webhookId),
            Math.Clamp(limit, 1, 100),
            cancellationToken);

        return Ok(runs.Select(r => new WebhookRunResponse(r)).ToList());
    }
}

// ── Request / Response DTOs ──────────────────────────────────────────────────

/// <summary>Request body for creating a webhook registration.</summary>
/// <param name="AgentId">Target agent ID.</param>
/// <param name="Label">Human-readable label for portal display.</param>
/// <param name="ConversationId">Optional conversation to pin all messages to.</param>
/// <param name="DefaultResponseMode">Response mode when not specified per-call. Defaults to Async.</param>
public sealed record CreateWebhookRegistrationRequest(
    string AgentId,
    string Label,
    string? ConversationId = null,
    WebhookResponseMode? DefaultResponseMode = null);

/// <summary>Request body for updating a webhook registration.</summary>
/// <param name="Label">New label. Null preserves existing.</param>
/// <param name="Enabled">New enabled state. Null preserves existing.</param>
/// <param name="DefaultResponseMode">New default response mode. Null preserves existing.</param>
public sealed record UpdateWebhookRegistrationRequest(
    string? Label = null,
    bool? Enabled = null,
    WebhookResponseMode? DefaultResponseMode = null);

/// <summary>
/// Webhook registration response. The <see cref="Secret"/> field is only populated
/// on the initial create response — it is never returned again.
/// </summary>
public sealed record WebhookRegistrationResponse
{
    /// <summary>Stable registration identifier.</summary>
    public string WebhookId { get; init; }

    /// <summary>Human-readable label.</summary>
    public string Label { get; init; }

    /// <summary>Target agent ID.</summary>
    public string AgentId { get; init; }

    /// <summary>Pinned conversation ID, if any.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Whether this registration accepts inbound POSTs.</summary>
    public bool Enabled { get; init; }

    /// <summary>Default response mode for inbound calls.</summary>
    public WebhookResponseMode DefaultResponseMode { get; init; }

    /// <summary>Inbound POST URL for this registration.</summary>
    public string Url { get; init; }

    /// <summary>Plaintext secret — only present on initial creation. Null on all subsequent reads.</summary>
    public string? Secret { get; init; }

    /// <summary>When the registration was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the registration last received an inbound POST.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Constructs a response from a <see cref="WebhookRegistration"/> record.</summary>
    /// <param name="r">The registration record.</param>
    /// <param name="url">Computed inbound URL.</param>
    /// <param name="secret">Plaintext secret (null except on initial create).</param>
    public WebhookRegistrationResponse(WebhookRegistration r, string url, string? secret)
    {
        WebhookId = r.Id.Value;
        Label = r.Label;
        AgentId = r.AgentId.Value;
        ConversationId = r.PinnedConversationId?.Value;
        Enabled = r.Enabled;
        DefaultResponseMode = r.DefaultResponseMode;
        Url = url;
        Secret = secret;
        CreatedAt = r.CreatedAt;
        LastUsedAt = r.LastUsedAt;
    }
}

/// <summary>Webhook run status response for polling after a 202 Accepted.</summary>
public sealed record WebhookRunResponse
{
    /// <summary>Run identifier.</summary>
    public string RunId { get; init; }

    /// <summary>Webhook registration that triggered this run.</summary>
    public string WebhookId { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public string Status { get; init; }

    /// <summary>When the inbound POST was accepted.</summary>
    public DateTimeOffset AcceptedAt { get; init; }

    /// <summary>When agent execution started. Null if still pending.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When agent execution completed. Null if still running.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Agent response text. Populated on Completed status.</summary>
    public string? AgentResponse { get; init; }

    /// <summary>Error message. Populated on Failed status.</summary>
    public string? Error { get; init; }

    /// <summary>Conversation ID used for this run.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Session ID in which the agent executed.</summary>
    public string? SessionId { get; init; }

    /// <summary>Constructs a response from a <see cref="WebhookRun"/> record.</summary>
    /// <param name="r">The run record.</param>
    public WebhookRunResponse(WebhookRun r)
    {
        RunId = r.Id.Value;
        WebhookId = r.WebhookId.Value;
        Status = r.Status.ToString();
        AcceptedAt = r.AcceptedAt;
        StartedAt = r.StartedAt;
        CompletedAt = r.CompletedAt;
        AgentResponse = r.AgentResponse;
        Error = r.Error;
        ConversationId = r.ConversationId?.Value;
        SessionId = r.SessionId?.Value;
    }
}
