using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

/// <summary>
/// Resolution seam for cron failure-alert targets (#2671). Deliberately a narrow, cron-owned
/// interface rather than a direct <c>IConversationStore</c> dependency: the cron assembly should
/// be able to ask the single question "does this conversation exist?" without taking on the
/// gateway's full conversation persistence surface. Mirrors the <see cref="ICronFailureAlertSink"/>
/// precedent, whose implementation likewise lives in the gateway assembly.
/// </summary>
public interface ICronAlertTargetResolver
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="conversationId"/> resolves to an existing
    /// conversation.
    /// </summary>
    /// <param name="conversationId">The candidate alert target.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><c>true</c> when the target resolves.</returns>
    Task<bool> ExistsAsync(ConversationId conversationId, CancellationToken ct = default);
}

/// <summary>
/// Outcome of a single alert-target validation. <see cref="Error"/> is non-null exactly when
/// <see cref="IsValid"/> is <c>false</c>.
/// </summary>
/// <param name="IsValid">Whether the target is acceptable.</param>
/// <param name="Error">Actionable rejection text naming the offending id, or <c>null</c>.</param>
public readonly record struct CronAlertTargetValidation(bool IsValid, string? Error)
{
    /// <summary>A passing result.</summary>
    public static CronAlertTargetValidation Valid { get; } = new(true, null);
}

/// <summary>
/// THE single validator for <see cref="CronJob.FailureAlertConversationId"/> (#2671).
///
/// <para>
/// Every authoring seam - <c>POST /api/cron</c>, <c>PUT /api/cron/{id}</c>, and the config-sync
/// materialiser - routes through <see cref="ValidateAsync"/> so create and update cannot drift
/// (the #2462 one-helper-many-phases precedent). Authoring validation is explicitly <b>not</b> a
/// replacement for the fire-time null guard in <c>CronScheduler.MaybeSendFailureAlertAsync</c>:
/// a conversation can be deleted after the job is stored, so both gates must remain.
/// </para>
/// </summary>
public static class CronAlertTarget
{
    /// <summary>
    /// Builds the rejection text for an unresolvable target. Naming the id is the point - a
    /// generic "validation failed" leaves the operator no way to find the typo.
    /// </summary>
    /// <param name="conversationId">The unresolvable conversation id.</param>
    /// <returns>The rejection message.</returns>
    public static string UnresolvableMessage(string conversationId)
        => $"FailureAlertConversationId '{conversationId}' does not resolve to an existing conversation. "
           + "Alerts for this job could never be delivered, so the job was not saved.";

    /// <summary>
    /// Rejection text used when a target was supplied but no resolver is available to check it.
    /// Fails CLOSED, matching <c>EnsureCommandAuthorized</c>: an unverifiable delivery target is
    /// refused rather than silently stored.
    /// </summary>
    /// <param name="conversationId">The unverifiable conversation id.</param>
    /// <returns>The rejection message.</returns>
    public static string UnverifiableMessage(string conversationId)
        => $"FailureAlertConversationId '{conversationId}' cannot be verified because no "
           + $"{nameof(ICronAlertTargetResolver)} is available; failing closed rather than storing an "
           + "alert target that may never deliver.";

    /// <summary>
    /// Validates a candidate failure-alert target.
    /// </summary>
    /// <param name="resolver">Resolver seam, or <c>null</c> when none is registered.</param>
    /// <param name="conversationId">Candidate target; <c>null</c> means alerting stays opt-in and is always valid.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The validation outcome.</returns>
    public static async Task<CronAlertTargetValidation> ValidateAsync(
        ICronAlertTargetResolver? resolver,
        ConversationId? conversationId,
        CancellationToken ct = default)
    {
        // Clause 3: a job with no alert target is unaffected. Alerting is opt-in and an absent
        // target must never be turned into a create-time failure.
        if (conversationId is not { } target)
            return CronAlertTargetValidation.Valid;

        if (resolver is null)
            return new CronAlertTargetValidation(false, UnverifiableMessage(target.Value));

        var exists = await resolver.ExistsAsync(target, ct).ConfigureAwait(false);
        return exists
            ? CronAlertTargetValidation.Valid
            : new CronAlertTargetValidation(false, UnresolvableMessage(target.Value));
    }
}
