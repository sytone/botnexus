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

    /// <summary>
    /// #2985: number of tool invocations the action's turn performed, or <c>null</c> when the
    /// action does not report a count at all.
    ///
    /// <para>
    /// The null-vs-zero distinction is load-bearing and deliberately not collapsed to a default of
    /// <c>0</c>. <c>null</c> means <i>unknown / not applicable</i> - a <c>command</c> or
    /// <c>webhook</c> action has no tool concept, and reading its silence as "zero tools" would
    /// classify every shell job on the platform as a do-nothing run. Only an action that actually
    /// counted its turn reports a value, and only then can the zero-tool rule fire.
    /// </para>
    /// </summary>
    public int? ToolInvocationCount { get; private set; }

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

    /// <summary>
    /// Records how many tool invocations this run's turn performed (#2985). Called by actions that
    /// can observe a tool timeline (currently only <c>agent-prompt</c>); the scheduler reads it to
    /// decide the terminal outcome for execution-class jobs. Not calling this leaves
    /// <see cref="ToolInvocationCount"/> <c>null</c>, which the scheduler treats as "no opinion"
    /// and maps to the pre-#2985 outcome.
    /// </summary>
    /// <param name="count">Tool invocations observed. Negative values are clamped to zero.</param>
    public void RecordToolInvocationCount(int count)
    {
        ToolInvocationCount = count < 0 ? 0 : count;
    }
}

public enum CronTriggerType
{
    Scheduled,
    Manual
}
