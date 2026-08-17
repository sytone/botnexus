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

    /// <summary>
    /// #3161: the error text of a failed <b>primary delivery</b>, or <c>null</c> when the action
    /// either delivered successfully or never attempted a delivery at all.
    ///
    /// <para>
    /// The null-vs-empty distinction is load-bearing for the same reason
    /// <see cref="ToolInvocationCount"/>'s is: <c>null</c> means <i>no delivery opinion</i>. A
    /// <c>command</c> or <c>webhook</c> action has no conversation-delivery concept, and reading its
    /// silence as a delivery failure would mark every shell job on the platform as undelivered.
    /// Only an action that actually routed a delivery through <see cref="DeliverAsync"/> can set it.
    /// </para>
    /// </summary>
    public string? DeliveryError { get; private set; }

    public void RecordSessionId(SessionId sessionId)
    {
        SessionId = sessionId;
    }

    /// <summary>
    /// #3161: THE seam through which an action performs its primary delivery, so that a delivery
    /// failure becomes a recorded run outcome instead of a silently discarded side effect.
    ///
    /// <para>
    /// Before #3161 delivery was a fire-and-forget side effect with no result channel back into
    /// <c>RunActionAsync</c>, so a run whose output reached nobody recorded <c>ok</c> with a null
    /// error - indistinguishable from a delivered run, and invisible to
    /// <c>CountConsecutiveErrorsAsync</c> because no error row was ever written.
    /// </para>
    /// <para>
    /// The failure is <b>contained here, not thrown</b>: letting it escape would surface the run as
    /// <see cref="CronRunStatus.Error"/>, which says "the job is broken" when the truth is "the job
    /// worked and its destination is gone". The scheduler folds
    /// <see cref="DeliveryError"/> into the terminal status instead.
    /// </para>
    /// <para>
    /// <see cref="OperationCanceledException"/> from a cancelled <paramref name="cancellationToken"/>
    /// is deliberately NOT contained. A gateway shutdown is not a delivery failure, and swallowing it
    /// would convert every restart into an alert storm while also robbing the scheduler's abort path
    /// of the cancellation it needs to record the run correctly.
    /// </para>
    /// <para>
    /// Recording is first-failure-wins: an action that delivers to several destinations keeps the
    /// original diagnosis rather than having it overwritten by a later, less informative one.
    /// </para>
    /// </summary>
    /// <param name="deliver">The delivery operation to perform.</param>
    /// <param name="cancellationToken">Host cancellation token; cancellation propagates.</param>
    public async Task DeliverAsync(Func<CancellationToken, Task> deliver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deliver);

        try
        {
            await deliver(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordDeliveryFailure(ex.Message);
        }
    }

    /// <summary>
    /// Records that this run's primary delivery failed, for actions whose delivery path cannot be
    /// expressed as a single awaitable (for example one that inspects a status code rather than
    /// throwing). First failure wins; blank text is ignored so a caller cannot accidentally mark a
    /// run undelivered with no diagnosis attached.
    /// </summary>
    /// <param name="error">Human-readable reason the delivery failed.</param>
    public void RecordDeliveryFailure(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return;

        DeliveryError ??= error;
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

    /// <summary>
    /// #2641: per-run cost measurements reported by the action, or a fully-null
    /// <see cref="CronRunCost"/> when the action reported nothing. Never null itself, so a consumer
    /// distinguishes "not measured" per field without also null-checking the container.
    /// </summary>
    public CronRunCost Cost { get; private set; } = new();

    /// <summary>
    /// Records what this run cost (#2641). Called by actions that can observe a turn's usage
    /// (currently only <c>agent-prompt</c>); <c>command</c> and <c>webhook</c> actions never call it
    /// and their runs therefore stay honestly unmeasured rather than being stamped with a zero that
    /// would rank them as the cheapest jobs on the platform.
    /// </summary>
    /// <remarks>
    /// Merge semantics are first-measurement-wins per field, mirroring
    /// <see cref="RecordDeliveryFailure"/>: a later report that measured nothing must not erase an
    /// earlier one that did. Negative values are clamped to zero - a negative token count is a
    /// producer bug, and propagating it would corrupt every total it is summed into.
    /// </remarks>
    /// <param name="cost">The measurements to fold in.</param>
    public void RecordCost(CronRunCost cost)
    {
        ArgumentNullException.ThrowIfNull(cost);

        Cost = new CronRunCost(
            TurnCount: Cost.TurnCount ?? ClampInt(cost.TurnCount),
            ToolCallCount: Cost.ToolCallCount ?? ClampInt(cost.ToolCallCount),
            DurationMs: Cost.DurationMs ?? ClampLong(cost.DurationMs),
            PromptTokens: Cost.PromptTokens ?? ClampLong(cost.PromptTokens),
            CompletionTokens: Cost.CompletionTokens ?? ClampLong(cost.CompletionTokens));
    }

    private static int? ClampInt(int? value) => value is null ? null : Math.Max(0, value.Value);

    private static long? ClampLong(long? value) => value is null ? null : Math.Max(0L, value.Value);

    /// <summary>
    /// Stamps the run's wall-clock duration (#2641). Kept separate from <see cref="RecordCost"/>
    /// because the scheduler - not the action - owns the clock: it brackets the whole action
    /// invocation including dispatch and timeout handling, so it measures what the run actually
    /// occupied rather than what the action noticed.
    /// </summary>
    /// <param name="durationMs">Elapsed milliseconds; negatives are clamped to zero.</param>
    public void RecordDuration(long durationMs)
    {
        Cost = Cost with { DurationMs = Math.Max(0L, durationMs) };
    }
}

public enum CronTriggerType
{
    Scheduled,
    Manual
}
