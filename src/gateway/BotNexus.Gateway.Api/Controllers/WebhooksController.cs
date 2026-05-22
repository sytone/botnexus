using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Webhook ingress endpoint that allows external services to trigger agent conversations via HTTP.
/// Requires a valid <c>X-BotNexus-Webhook-Key</c> header on all requests.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class WebhooksController : ControllerBase
{
    private readonly IAgentSupervisor _supervisor;
    private readonly IConversationStore _conversations;
    private readonly IOptions<WebhookOptions> _options;
    private readonly IEnumerable<IConversationChangeNotifier> _conversationChangeNotifiers;
    private readonly ILogger<WebhooksController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebhooksController"/> class.
    /// </summary>
    public WebhooksController(
        IAgentSupervisor supervisor,
        IConversationStore conversations,
        IOptions<WebhookOptions> options,
        IEnumerable<IConversationChangeNotifier>? conversationChangeNotifiers = null,
        ILogger<WebhooksController>? logger = null)
    {
        _supervisor = supervisor;
        _conversations = conversations;
        _options = options;
        _conversationChangeNotifiers = conversationChangeNotifiers ?? Enumerable.Empty<IConversationChangeNotifier>();
        _logger = logger ?? NullLogger<WebhooksController>.Instance;
    }

    /// <summary>
    /// Injects a user message into an agent conversation.
    /// When <c>conversationId</c> is omitted, a new conversation is created.
    /// The agent turn is dispatched asynchronously — this endpoint returns immediately after queuing.
    /// </summary>
    /// <param name="request">The webhook message request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>202 Accepted with conversationId, or an error status code.</returns>
    [HttpPost("message")]
    [ProducesResponseType(typeof(WebhookMessageResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Message(
        [FromBody] WebhookMessageRequest request,
        CancellationToken cancellationToken)
    {
        // Validate key
        var keyHeader = HttpContext.Request.Headers["X-BotNexus-Webhook-Key"].FirstOrDefault();
        if (!ValidateWebhookKey(keyHeader, request.AgentId, out var keyError))
        {
            _logger.LogWarning("Webhook request rejected: {Reason}", keyError);
            return Unauthorized(new { error = keyError });
        }

        if (string.IsNullOrWhiteSpace(request.AgentId))
            return BadRequest(new { error = "agentId is required." });

        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required." });

        // Resolve or create conversation
        Conversation conversation;
        if (!string.IsNullOrWhiteSpace(request.ConversationId))
        {
            var existing = await _conversations.GetAsync(ConversationId.From(request.ConversationId), cancellationToken);
            if (existing is null)
                return NotFound(new { error = $"Conversation '{request.ConversationId}' not found." });
            conversation = existing;
        }
        else
        {
            conversation = new Conversation
            {
                ConversationId = ConversationId.Create(),
                AgentId = AgentId.From(request.AgentId),
                Title = string.IsNullOrWhiteSpace(request.ConversationTitle) ? "Webhook conversation" : request.ConversationTitle,
                Purpose = string.IsNullOrWhiteSpace(request.ConversationPurpose) ? null : request.ConversationPurpose,
                Status = ConversationStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            conversation = await _conversations.CreateAsync(conversation, cancellationToken);
            _logger.LogDebug("Webhook created new conversation {ConversationId} for agent {AgentId}", conversation.ConversationId.Value, request.AgentId);
        }

        var sessionId = SessionId.From(Guid.NewGuid().ToString("N"));
        var agentId = AgentId.From(request.AgentId);

        try
        {
            var handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, CancellationToken.None);

            // Dispatch fire-and-forget — webhook callers do not wait for LLM response
            _ = Task.Run(async () =>
            {
                try
                {
                    await handle.PromptAsync(request.Message, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Webhook agent turn failed for conversation {ConversationId}", conversation.ConversationId.Value);
                }
            }, CancellationToken.None);

            // Notify portal about new conversation
            foreach (var notifier in _conversationChangeNotifiers)
            {
                try { await notifier.NotifyConversationChangedAsync("created", agentId.Value, conversation.ConversationId.Value, cancellationToken); }
                catch { /* best-effort */ }
            }

            _logger.LogInformation("Webhook message dispatched for agent {AgentId}, conversation {ConversationId}", request.AgentId, conversation.ConversationId.Value);
            return Accepted(new WebhookMessageResponse(conversation.ConversationId.Value, sessionId.Value));
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

    private bool ValidateWebhookKey(string? providedKey, string? agentId, out string error)
    {
        var config = _options.Value;

        if (!config.Enabled)
        {
            error = "Webhook ingress is disabled.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(providedKey))
        {
            error = "X-BotNexus-Webhook-Key header is required.";
            return false;
        }

        if (config.Keys is null || config.Keys.Count == 0)
        {
            error = "No webhook keys configured.";
            return false;
        }

        foreach (var entry in config.Keys)
        {
            if (entry.Key != providedKey)
                continue;

            // Check agent scope restriction if set
            if (entry.AllowedAgents is { Count: > 0 } && !string.IsNullOrWhiteSpace(agentId))
            {
                if (!entry.AllowedAgents.Contains(agentId, StringComparer.OrdinalIgnoreCase))
                {
                    error = $"Webhook key is not authorized for agent '{agentId}'.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        error = "Invalid webhook key.";
        return false;
    }
}

/// <summary>Webhook message request payload.</summary>
public sealed record WebhookMessageRequest(
    string AgentId,
    string Message,
    string? ConversationId = null,
    string? ConversationTitle = null,
    string? ConversationPurpose = null);

/// <summary>Webhook message response payload.</summary>
public sealed record WebhookMessageResponse(string ConversationId, string SessionId);
