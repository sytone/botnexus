namespace BotNexus.Gateway.Abstractions.Notifications;

/// <summary>
/// What a notification is about. Drives the icon and grouping a client shows, and lets a caller
/// filter without parsing prose.
/// </summary>
public enum NotificationKind
{
    /// <summary>An agent finished a run.</summary>
    AgentRunCompleted = 0,

    /// <summary>An agent's run ended in an error.</summary>
    AgentRunFailed = 1,

    /// <summary>
    /// An agent is blocked waiting for a person to answer. The one kind where the work is stopped
    /// until someone acts, which is why it is worth waking a phone for.
    /// </summary>
    AgentWaitingForInput = 2,

    /// <summary>A scheduled run finished, successfully or not.</summary>
    CronRunOutcome = 3,

    /// <summary>The gateway's own health changed - degraded, an adapter down, a watchdog firing.</summary>
    GatewayHealth = 4,
}

/// <summary>How much the notification wants attention.</summary>
public enum NotificationSeverity
{
    /// <summary>Something finished as expected.</summary>
    Info = 0,

    /// <summary>Something needs attention but nothing is broken.</summary>
    Warning = 1,

    /// <summary>Something failed.</summary>
    Error = 2,
}

/// <summary>
/// One notification, as stored and as every client reads it.
/// </summary>
/// <remarks>
/// Deliberately transport-agnostic. The portal renders these today; a phone or desktop app reading
/// the same records later is the point of storing them server-side rather than in the browser. That
/// is also why read state lives here and not in localStorage - dismissing something on a laptop
/// should not leave it unread on a phone.
/// </remarks>
public sealed record Notification
{
    /// <summary>Stable identifier.</summary>
    public required string Id { get; init; }

    /// <summary>What the notification is about.</summary>
    public required NotificationKind Kind { get; init; }

    /// <summary>How much attention it wants.</summary>
    public required NotificationSeverity Severity { get; init; }

    /// <summary>One-line summary. Written to be readable on a lock screen, not just in the portal.</summary>
    public required string Title { get; init; }

    /// <summary>Optional detail shown when the notification is expanded.</summary>
    public string? Body { get; init; }

    /// <summary>Agent the notification concerns, when it concerns one.</summary>
    public string? AgentId { get; init; }

    /// <summary>Conversation the notification concerns, when it concerns one.</summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Site-relative path taking the reader to whatever the notification is about, or <c>null</c>
    /// when there is nowhere useful to go.
    /// </summary>
    public string? Link { get; init; }

    /// <summary>When it was raised.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>When it was read, or <c>null</c> while unread.</summary>
    public DateTimeOffset? ReadAtUtc { get; init; }
}
