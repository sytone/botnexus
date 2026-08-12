using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

public sealed record CronJob
{
    public required JobId Id { get; init; }
    public required string Name { get; init; }
    public required string Schedule { get; init; }
    public required string ActionType { get; init; }
    public AgentId? AgentId { get; init; }
    public string? Message { get; init; }
    /// <summary>
    /// Optional named prompt template reference for agent-prompt jobs.
    /// When set, the runtime resolves and renders this template at execution time.
    /// </summary>
    public string? TemplateName { get; init; }

    /// <summary>
    /// Optional parameter values applied when rendering <see cref="TemplateName"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? TemplateParameters { get; init; }
    public string? Model { get; init; }
    public string? WebhookUrl { get; init; }
    public string? ShellCommand { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Whether this is a system-provisioned job (e.g., heartbeat). Hidden from default listings.</summary>
    public bool System { get; init; }

    /// <summary>
    /// Opt-in cleanup for ephemeral jobs: when <c>true</c>, the scheduler deletes the run's
    /// agent session and its transcript after the run completes (across success / timeout /
    /// error / abort), provided the run produced a cron-scoped (<c>cron:</c>) session.
    ///
    /// This prevents run-scoped cron sessions from accumulating transcript entries indefinitely
    /// (the unbounded-growth class behind long-lived reporting sessions). It is <b>off by default</b>:
    /// long-lived reporting jobs (heartbeat, maintenance) that intentionally persist context across
    /// runs must NOT enable this -- for those, compaction/truncation is the right lever. Deletion only
    /// targets sessions whose id begins with <c>cron:</c>, so a misconfigured flag cannot remove an
    /// unrelated long-lived session.
    /// </summary>
    public bool DeleteAfterRun { get; init; }
    public string? TimeZone { get; init; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public DateTimeOffset? NextRunAt { get; init; }
    public string? LastRunStatus { get; init; }
    public string? LastRunError { get; init; }
    /// <summary>
    /// Canonical link from a cron job to its long-lived Conversation. P9-D inverts the
    /// previous "composite-id key" model: the job owns the link, and every run lands in
    /// that one conversation until the job is deleted.
    ///
    /// Null on creation. Stamped via CAS during the first run that requires a per-job
    /// conversation (currently only the agent-prompt action routed through CronTrigger;
    /// heartbeat and soul triggers manage their own per-agent conversations). Once
    /// stamped, immutable for the life of the job — operators wanting a fresh
    /// conversation thread delete the job and create a new one.
    /// </summary>
    public ConversationId? ConversationId { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>
    /// Instant at which the job's <b>current</b> scheduling inputs (<see cref="Schedule"/> and
    /// <see cref="TimeZone"/>) took effect. Missed-run detection clamps its scan floor to this
    /// value so occurrences computed from a schedule that was not active at the time are never
    /// replayed as missed runs (#2554).
    ///
    /// <b>Store-owned.</b> It is stamped exclusively by <c>ICronStore</c> on create and on a
    /// definition update that actually changes <see cref="Schedule"/> or <see cref="TimeZone"/>.
    /// A caller-supplied value on any create/update payload is discarded: honouring one would let
    /// an import or a crafted <c>POST /api/cron</c> spoof catch-up ownership and force an immediate
    /// execution of an agent prompt or shell command.
    ///
    /// <c>null</c> means <i>unknown</i> - a row written before this column existed. Unknown is
    /// deliberately treated as "no clamp", i.e. exactly today's behaviour, so the migration cannot
    /// retroactively suppress legitimate missed runs for pre-existing jobs.
    /// </summary>
    public DateTimeOffset? ScheduleActivatedAt { get; init; }

    /// <summary>
    /// Opt-in per-job failure alerting (#2557). When <c>true</c> and
    /// <see cref="FailureAlertConversationId"/> is set, a run that terminates as
    /// <see cref="CronRunStatus.Error"/> delivers a <see cref="CronFailureAlert"/> to that
    /// conversation - on the FIRST failure of a streak and then on a doubling backoff, never on
    /// every run (a job failing every minute would otherwise become the noise it was meant to
    /// detect).
    ///
    /// <b>Off by default.</b> Rows written before this column existed read as <c>false</c>, which
    /// is byte-identical to today's behaviour: no delivery at all.
    /// </summary>
    public bool FailureAlertsEnabled { get; init; }

    /// <summary>
    /// Conversation that failure alerts for this job are delivered to. <c>null</c> means no
    /// target configured, which disables delivery regardless of
    /// <see cref="FailureAlertsEnabled"/> - there is deliberately no implicit fallback to
    /// <see cref="ConversationId"/>, so enabling alerts can never retarget a job's own
    /// long-lived run conversation by accident.
    /// </summary>
    public ConversationId? FailureAlertConversationId { get; init; }

    /// <summary>
    /// Opt-in <b>job-level</b> one-shot disposition (#2634). When <c>true</c>, the scheduler deletes
    /// the <b>job itself</b> after its first terminal run - success, timeout, error, or host abort
    /// alike - from the same post-run <c>finally</c> that already owns run teardown.
    ///
    /// <para>
    /// This is deliberately <b>not</b> <see cref="DeleteAfterRun"/>, which deletes the run's ephemeral
    /// <i>session</i> and leaves the job scheduled forever (#1561). The two coexist and compose: a job
    /// may set both, neither, or either.
    /// </para>
    /// <para>
    /// The whole point is that removal is <b>scheduler-driven, not prompt-driven</b>. The defect behind
    /// #2634 was a job whose prompt said "delete this cron job after running": the agent ended its turn
    /// without doing so and the job stayed scheduled for another year. An instruction in prose has no
    /// enforcement and no retry; this flag does.
    /// </para>
    /// <para>
    /// <b>Off by default.</b> Rows written before this column existed read as <c>false</c>, which is
    /// byte-identical to today's behaviour: nothing is ever removed. There is no path by which an
    /// existing job is silently deleted without an explicit opt-in.
    /// </para>
    /// </summary>
    public bool DeleteJobAfterRun { get; init; }

    /// <summary>
    /// Optional hard expiry instant (#2634). Once <c>now &gt;= ExpiresAt</c> the job <b>stops executing</b>:
    /// the scheduler suppresses the fire and never invokes the action.
    ///
    /// <para>
    /// Expiry <b>suppresses</b>; it does not delete or disable the row. The job stays visible in
    /// <c>cron list</c> with its history intact so a human can see what expired and extend it, and
    /// nothing about the stored job is silently mutated (#2634 out-of-scope: never disable or delete
    /// an existing job implicitly). Pair <see cref="ExpiresAt"/> with <see cref="DeleteJobAfterRun"/>
    /// if removal is actually wanted.
    /// </para>
    /// <para>
    /// The check is applied at BOTH schedule time (the due-scan skips an expired job) and fire time
    /// (immediately before the run is stamped, inside <c>RunActionAsync</c>). Schedule time alone would
    /// leak: a job already past due, a manual <c>RunNowAsync</c>, or an expiry that elapses between the
    /// due-scan and execution would all still fire. Fire time is therefore the authoritative gate and
    /// schedule time is the cheap early-out.
    /// </para>
    /// <para>
    /// <c>null</c> means <i>no expiry</i> - exactly today's behaviour, no clamp and no suppression -
    /// mirroring the <see cref="ScheduleActivatedAt"/> (#2554) NULL-means-unknown rule. A row written
    /// before this column existed reads NULL and is therefore untouched.
    /// </para>
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Opt-in <b>execution-class</b> marker (#2985). Declares that this job's contract is to
    /// <i>perform work</i>, so a run of it that completes having made <b>zero tool invocations</b>
    /// has by definition done nothing and must not be recorded as
    /// <see cref="CronRunStatus.Ok"/>. Such a run terminates as
    /// <see cref="CronRunStatus.NoToolCalls"/> and flows through the existing
    /// <see cref="FailureAlertConversationId"/> path like any other non-success outcome - there is
    /// deliberately no second notification channel.
    ///
    /// <para>
    /// The marker exists because the rule cannot be applied blindly to every <c>agent-prompt</c>
    /// job: a genuine reporting or classification job may legitimately answer from context with no
    /// tool call at all, and flagging those would make the signal worthless. The operator declares
    /// the class; the scheduler enforces it.
    /// </para>
    /// <para>
    /// <b>Off by default.</b> Rows written before this column existed read as <c>false</c>, which
    /// is byte-identical to today's behaviour: zero-tool runs of an unmarked job still record
    /// <c>ok</c>. It is also inert for action types that report no tool count (<c>command</c>,
    /// <c>webhook</c>) - see <c>CronExecutionContext.ToolInvocationCount</c>, where <c>null</c>
    /// means "not reported" and is never read as zero.
    /// </para>
    /// </summary>
    public bool ExecutionClass { get; init; }
}
