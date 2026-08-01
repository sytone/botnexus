using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

/// <summary>
/// Payload for an opt-in cron failure alert (#2557). Carries the <b>scheduled</b> run time
/// alongside the actual attempt time: without the scheduled occurrence the recipient cannot
/// tell <i>which</i> occurrence broke, which is the entire point of the alert.
/// </summary>
/// <param name="JobId">Identifier of the failing job.</param>
/// <param name="JobName">Human-readable job name.</param>
/// <param name="ScheduledRunTime">The occurrence the run was triggered for.</param>
/// <param name="AttemptedAt">Wall-clock instant the failure was observed.</param>
/// <param name="ConsecutiveErrorCount">Length of the current error streak, 1 for the first failure.</param>
/// <param name="Error">
/// Error text already passed through <see cref="CronExternalDeliveryRedactor.RedactSummary"/>.
/// Never assign raw run output here.
/// </param>
public sealed record CronFailureAlert(
    JobId JobId,
    string JobName,
    DateTimeOffset ScheduledRunTime,
    DateTimeOffset AttemptedAt,
    int ConsecutiveErrorCount,
    string? Error)
{
    /// <summary>
    /// Renders the alert as the operator-facing message body delivered to the configured
    /// conversation. Timestamps use round-trip ("O") format so the scheduled occurrence is
    /// unambiguous across time zones.
    /// </summary>
    public string FormatMessage()
    {
        var lines = new List<string>
        {
            $"Cron job failed: {JobName} ({JobId.Value})",
            $"Scheduled run time: {ScheduledRunTime:O}",
            $"Attempted at: {AttemptedAt:O}",
            $"Consecutive errors: {ConsecutiveErrorCount}",
        };

        if (!string.IsNullOrWhiteSpace(Error))
            lines.Add($"Error: {Error}");

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Delivery seam for cron failure alerts. The gateway implementation posts the rendered
/// message into the configured conversation through the existing inbound-message
/// orchestrator; no webhook or per-channel routing is involved (deliberately out of scope
/// for #2557).
/// </summary>
/// <remarks>
/// Implementations may throw: <see cref="CronScheduler"/> treats a delivery failure as
/// non-fatal to the cron run itself and logs it (AC7).
/// </remarks>
public interface ICronFailureAlertSink
{
    /// <summary>Delivers <paramref name="alert"/> to <paramref name="conversationId"/>.</summary>
    Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default);
}
