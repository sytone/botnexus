namespace BotNexus.Cron;

public interface ICronAction
{
    string ActionType { get; }
    Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default);
}

public sealed record CronExecutionContext
{
    public required CronJob Job { get; init; }
    public required string RunId { get; init; }
    public required DateTimeOffset TriggeredAt { get; init; }
    public required CronTriggerType TriggerType { get; init; }
    public required IServiceProvider Services { get; init; }
    public string? SessionId { get; private set; }

    /// <summary>
    /// The conversation ID resolved or created by the trigger for this run.
    /// Set by the trigger so the scheduler can persist it back to the job record.
    /// </summary>
    public string? ConversationId { get; private set; }

    public void RecordSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
    }

    /// <summary>
    /// Records the conversation ID resolved for this cron run.
    /// Called by the trigger after conversation creation or lookup so the scheduler
    /// can persist the value back to the job for fast-path reuse on subsequent runs.
    /// </summary>
    public void RecordConversationId(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ConversationId = conversationId;
    }
}

public enum CronTriggerType
{
    Scheduled,
    Manual
}