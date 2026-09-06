namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Lifecycle status of a single webhook run.
/// </summary>
public enum WebhookRunStatus
{
    /// <summary>Run has been accepted but dispatch has not yet been attempted.</summary>
    Pending,

    /// <summary>
    /// The delivery is WAITING for the target agent's single execution slot and has not begun
    /// executing (#3851).
    /// </summary>
    /// <remarks>
    /// This state exists because <see cref="Running"/> used to cover both cases. A delivery blocked
    /// behind another turn's session write lock reported <see cref="Running"/> throughout, so a run
    /// waiting on a mutex was indistinguishable from one actively producing work - which is exactly
    /// the observability gap #3851 reports. A run reaches this state only when it could not be
    /// admitted immediately; an uncontended run goes straight to <see cref="Running"/>.
    /// </remarks>
    Queued,

    /// <summary>Agent is currently executing - the execution slot has been acquired.</summary>
    Running,

    /// <summary>Agent completed successfully.</summary>
    Completed,

    /// <summary>Agent run failed with an error.</summary>
    Failed,

    /// <summary>The run exceeded its configured timeout (sync deadline or background run ceiling).</summary>
    Timeout,

    /// <summary>
    /// The delivery was refused because the target agent's bounded inbound queue was full (#3851).
    /// No agent run was started and the message was not delivered, so a retry is safe.
    /// </summary>
    Rejected
}
