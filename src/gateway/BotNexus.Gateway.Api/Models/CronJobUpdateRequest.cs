using BotNexus.Cron;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Api.Models;

/// <summary>
/// Request body for <c>PUT /api/cron/{jobId}</c> (#3808).
/// </summary>
/// <remarks>
/// <para>
/// Every mutable property is a <see cref="CronPatch{T}"/>, so the endpoint can tell "the caller did
/// not mention this field" from "the caller asked for the default". Binding the domain record
/// <see cref="CronJob"/> directly could not: an omitted <c>bool</c> arrived as <c>false</c> and an
/// omitted reference as <c>null</c>, and the controller wrote both straight over the stored row.
/// A portal edit of a job's schedule therefore cleared its failure alerting, its one-shot
/// disposition, its expiry and its execution class - silently, and precisely in the state where the
/// loss is least likely to be noticed, because a job that has stopped alerting reports nothing.
/// </para>
/// <para>
/// The rule this restores is not new. The <c>CronTool</c> update seam has applied it since #2634 /
/// #2838 / #2985, field by field, with explicit comments saying an omitted field must leave the
/// stored value alone. This is the REST half of that rule, which never adopted it.
/// </para>
/// <para>
/// Deliberately absent members: <c>Id</c> (the route wins), <c>CreatedAt</c> and <c>CreatedBy</c>
/// (server-stamped provenance, always taken from the stored row - #3575), <c>ScheduleActivatedAt</c>
/// (store-owned; honouring an inbound value would let a crafted request spoof catch-up ownership
/// and force an immediate execution - #2554), and the scheduler-owned runtime bookkeeping
/// <c>LastRun*</c> / <c>NextRunAt</c> / <c>BackoffUntil</c> / <c>ConversationId</c> (#2133). Those
/// are not omitted-field cases at all: they are columns no REST caller authors, so representing
/// them here would reintroduce the write path this endpoint exists to keep closed.
/// </para>
/// </remarks>
public sealed record CronJobUpdateRequest
{
    /// <summary>Job display name.</summary>
    public CronPatch<string> Name { get; init; }

    /// <summary>Standard 5-field cron expression.</summary>
    public CronPatch<string> Schedule { get; init; }

    /// <summary>Action performed when the job fires.</summary>
    public CronPatch<string> ActionType { get; init; }

    /// <summary>Target agent identifier.</summary>
    public CronPatch<string> AgentId { get; init; }

    /// <summary>Prompt message for agent-prompt jobs.</summary>
    public CronPatch<string> Message { get; init; }

    /// <summary>Named prompt template reference.</summary>
    public CronPatch<string> TemplateName { get; init; }

    /// <summary>Parameter values applied when rendering the template.</summary>
    public CronPatch<IReadOnlyDictionary<string, string?>> TemplateParameters { get; init; }

    /// <summary>Optional model override.</summary>
    public CronPatch<string> Model { get; init; }

    /// <summary>Webhook target for webhook jobs.</summary>
    public CronPatch<string> WebhookUrl { get; init; }

    /// <summary>Shell command executed by <c>command</c> jobs.</summary>
    public CronPatch<string> ShellCommand { get; init; }

    /// <summary>Whether the job is enabled.</summary>
    public CronPatch<bool> Enabled { get; init; }

    /// <summary>Whether this is a system-provisioned job.</summary>
    public CronPatch<bool> System { get; init; }

    /// <summary>IANA timezone the schedule is evaluated in.</summary>
    public CronPatch<string> TimeZone { get; init; }

    /// <summary>Arbitrary job metadata.</summary>
    public CronPatch<IReadOnlyDictionary<string, object?>> Metadata { get; init; }

    /// <summary>
    /// Next scheduled run. Range-validated as on create; the controller only honours it when the
    /// caller actually supplied one.
    /// </summary>
    public CronPatch<DateTimeOffset?> NextRunAt { get; init; }

    /// <summary>Opt-in per-job failure alerting (#2557). Omitting this leaves the stored value alone.</summary>
    public CronPatch<bool> FailureAlertsEnabled { get; init; }

    /// <summary>
    /// Conversation failure alerts are delivered to (#2557). Omitting this leaves the stored value
    /// alone; an explicit <c>null</c> or empty string clears it, matching the tool seam's spelling.
    /// </summary>
    public CronPatch<string> FailureAlertConversationId { get; init; }

    /// <summary>Job-level one-shot disposition (#2634). Omitting this leaves the stored value alone.</summary>
    public CronPatch<bool> DeleteJobAfterRun { get; init; }

    /// <summary>Ephemeral run-session cleanup (#1561). Omitting this leaves the stored value alone.</summary>
    public CronPatch<bool> DeleteAfterRun { get; init; }

    /// <summary>
    /// Hard expiry instant (#2634). Omitting this leaves the stored value alone; an explicit
    /// <c>null</c> or empty string clears it.
    /// </summary>
    public CronPatch<DateTimeOffset?> ExpiresAt { get; init; }

    /// <summary>Execution-class marker (#2985). Omitting this leaves the stored value alone.</summary>
    public CronPatch<bool> ExecutionClass { get; init; }

    /// <summary>
    /// Projects a full <see cref="CronJob"/> onto a request in which <b>every</b> field is
    /// explicitly set.
    /// </summary>
    /// <remarks>
    /// This is the round-trip shape a client produces when it GETs a job, edits one field and PUTs
    /// the whole record back - the one case where the old direct-binding behaviour was already
    /// correct, because nothing was omitted. It exists so that intent can be stated explicitly at a
    /// call site rather than inferred from a record that happens to be fully populated.
    /// </remarks>
    /// <param name="job">The job whose values become the explicit request.</param>
    /// <returns>A request with every property set.</returns>
    public static CronJobUpdateRequest FromCronJob(CronJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new CronJobUpdateRequest
        {
            Name = CronPatch<string>.Set(job.Name),
            Schedule = CronPatch<string>.Set(job.Schedule),
            ActionType = CronPatch<string>.Set(job.ActionType),
            AgentId = CronPatch<string>.Set(job.AgentId?.Value),
            Message = CronPatch<string>.Set(job.Message),
            TemplateName = CronPatch<string>.Set(job.TemplateName),
            TemplateParameters = CronPatch<IReadOnlyDictionary<string, string?>>.Set(job.TemplateParameters),
            Model = CronPatch<string>.Set(job.Model),
            WebhookUrl = CronPatch<string>.Set(job.WebhookUrl),
            ShellCommand = CronPatch<string>.Set(job.ShellCommand),
            Enabled = CronPatch<bool>.Set(job.Enabled),
            System = CronPatch<bool>.Set(job.System),
            TimeZone = CronPatch<string>.Set(job.TimeZone),
            Metadata = CronPatch<IReadOnlyDictionary<string, object?>>.Set(job.Metadata),
            NextRunAt = CronPatch<DateTimeOffset?>.Set(job.NextRunAt),
            FailureAlertsEnabled = CronPatch<bool>.Set(job.FailureAlertsEnabled),
            FailureAlertConversationId = CronPatch<string>.Set(job.FailureAlertConversationId?.Value),
            DeleteJobAfterRun = CronPatch<bool>.Set(job.DeleteJobAfterRun),
            DeleteAfterRun = CronPatch<bool>.Set(job.DeleteAfterRun),
            ExpiresAt = CronPatch<DateTimeOffset?>.Set(job.ExpiresAt),
            ExecutionClass = CronPatch<bool>.Set(job.ExecutionClass)
        };
    }

    /// <summary>
    /// Applies this request to the stored job, preserving every field the caller did not mention.
    /// </summary>
    /// <param name="existing">The stored job, which supplies every unmentioned value.</param>
    /// <returns>The job to persist, before controller-level normalization and governed columns.</returns>
    public CronJob ApplyTo(CronJob existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var requestedAgentId = AgentId.Or(existing.AgentId?.Value);

        return existing with
        {
            Name = Name.Or(existing.Name) ?? existing.Name,
            Schedule = Schedule.Or(existing.Schedule) ?? existing.Schedule,
            ActionType = ActionType.Or(existing.ActionType) ?? existing.ActionType,
            AgentId = string.IsNullOrWhiteSpace(requestedAgentId)
                ? null
                : Domain.Primitives.AgentId.From(requestedAgentId),
            Message = Message.Or(existing.Message),
            TemplateName = TemplateName.Or(existing.TemplateName),
            TemplateParameters = TemplateParameters.Or(existing.TemplateParameters),
            Model = Model.Or(existing.Model),
            WebhookUrl = WebhookUrl.Or(existing.WebhookUrl),
            ShellCommand = ShellCommand.Or(existing.ShellCommand),
            Enabled = Enabled.IsSet ? Enabled.Value : existing.Enabled,
            System = System.IsSet ? System.Value : existing.System,
            TimeZone = TimeZone.Or(existing.TimeZone),
            Metadata = Metadata.Or(existing.Metadata),
            NextRunAt = NextRunAt.IsSet ? NextRunAt.Value : existing.NextRunAt,

            // The six fields this issue is about. Each mirrors the CronTool spelling exactly:
            // omitted preserves, explicit mutates, and for the two nullable ones an explicit
            // null/blank clears.
            FailureAlertsEnabled = FailureAlertsEnabled.IsSet
                ? FailureAlertsEnabled.Value
                : existing.FailureAlertsEnabled,
            FailureAlertConversationId = ResolveAlertConversationId(existing),
            DeleteJobAfterRun = DeleteJobAfterRun.IsSet ? DeleteJobAfterRun.Value : existing.DeleteJobAfterRun,
            DeleteAfterRun = DeleteAfterRun.IsSet ? DeleteAfterRun.Value : existing.DeleteAfterRun,
            ExpiresAt = ExpiresAt.IsSet ? ExpiresAt.Value : existing.ExpiresAt,
            ExecutionClass = ExecutionClass.IsSet ? ExecutionClass.Value : existing.ExecutionClass
        };
    }

    /// <summary>
    /// The alert target after applying this request: omitted preserves, an explicit blank clears.
    /// </summary>
    /// <remarks>
    /// Blank-means-clear is the <c>CronTool.ParseAlertConversationId</c> spelling (#2838), repeated
    /// here rather than invented: the two seams must agree on what an empty string means or a
    /// caller migrating between them silently changes their job's alerting.
    /// </remarks>
    /// <param name="existing">The stored job.</param>
    /// <returns>The conversation id to persist.</returns>
    public ConversationId? ResolveAlertConversationId(CronJob existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        if (!FailureAlertConversationId.IsSet)
            return existing.FailureAlertConversationId;

        var raw = FailureAlertConversationId.Value;
        return string.IsNullOrWhiteSpace(raw) ? null : ConversationId.From(raw);
    }
}
