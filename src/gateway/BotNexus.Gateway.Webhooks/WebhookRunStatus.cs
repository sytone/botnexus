namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Lifecycle status of a single webhook run.
/// </summary>
public enum WebhookRunStatus
{
    /// <summary>Run has been accepted and is queued for processing.</summary>
    Pending,

    /// <summary>Agent is currently executing.</summary>
    Running,

    /// <summary>Agent completed successfully.</summary>
    Completed,

    /// <summary>Agent run failed with an error.</summary>
    Failed,

    /// <summary>Sync-mode run exceeded the configured timeout.</summary>
    Timeout
}
