using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

public interface ICronAction
{
    string ActionType { get; }
    Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default);
}

public sealed record CronExecutionContext
{
    public required CronJob Job { get; init; }
    public required RunId RunId { get; init; }
    public required DateTimeOffset TriggeredAt { get; init; }
    public required CronTriggerType TriggerType { get; init; }
    public required IServiceProvider Services { get; init; }
    public SessionId? SessionId { get; private set; }

    /// <summary>
    /// The conversation ID resolved or created by the trigger for this run.
    /// Set by the trigger so the scheduler can persist it back to the job record.
    /// </summary>
    public ConversationId? ConversationId { get; private set; }

    public void RecordSessionId(SessionId sessionId)
    {
        SessionId = sessionId;
    }

    /// <summary>
    /// Records the conversation ID resolved for this cron run.
    /// Called by the trigger after conversation creation or lookup so the scheduler
    /// can persist the value back to the job for fast-path reuse on subsequent runs.
    /// </summary>
    public void RecordConversationId(ConversationId conversationId)
    {
        ConversationId = conversationId;
    }
}

public enum CronTriggerType
{
    Scheduled,
    Manual
}
