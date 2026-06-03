using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Record of a single inbound webhook POST — one registration POST = one run.
/// Persisted so callers can poll status after a 202 response.
/// </summary>
public sealed record WebhookRun
{
    public required WebhookRunId Id { get; init; }
    public required WebhookId WebhookId { get; init; }

    /// <summary>Conversation resolved or created for this run.</summary>
    public ConversationId? ConversationId { get; init; }

    /// <summary>Session in which the agent executed (set when run starts).</summary>
    public SessionId? SessionId { get; init; }

    public required WebhookRunStatus Status { get; set; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Agent response text (populated on <see cref="WebhookRunStatus.Completed"/>).</summary>
    public string? AgentResponse { get; set; }

    /// <summary>Error message (populated on <see cref="WebhookRunStatus.Failed"/>).</summary>
    public string? Error { get; set; }

    /// <summary>Callback URL to POST results to (callback mode only).</summary>
    public string? CallbackUrl { get; init; }

    /// <summary>Whether the agent was asked to process the message (vs. store-only).</summary>
    public bool AgentAction { get; init; } = true;
}
