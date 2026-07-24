using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Cron;

/// <summary>
/// Cron-side redaction boundary for external delivery (#1752). When cron external delivery
/// (webhook / <c>cron_changed</c> fan-out) is eventually wired, command/agent output summaries and
/// diagnostics MUST be routed through this helper before any external POST leaves the box.
///
/// This is a <b>forward-design guard</b>: today <see cref="Actions.WebhookAction"/> still throws
/// <see cref="NotSupportedException"/> and no external delivery path exists, so there is no live leak.
/// The primitive is baked in now (with tests) so that whoever wires delivery cannot forget to redact.
///
/// Two invariants:
/// <list type="bullet">
///   <item>The external copy is scrubbed via <see cref="ISecretRedactor.RedactForExternalDelivery(string)"/>
///   (device codes, action URLs, key=value secrets, plus every base secret pattern).</item>
///   <item>Embedded diagnostics (a job's <c>lastDiagnostics</c>-equivalent metadata) are stripped from
///   the external serialized job entirely; only the local operator record keeps the full unredacted
///   output.</item>
/// </list>
/// </summary>
public static class CronExternalDeliveryRedactor
{
    /// <summary>
    /// Metadata keys that carry internal diagnostic detail (stack traces, last-diagnostics blobs)
    /// and must be stripped from any job before it is serialized for external delivery. Matched
    /// case-insensitively; a key containing "diagnostic" is always treated as diagnostic material.
    /// </summary>
    private static readonly string[] DiagnosticMetadataKeys =
    {
        "lastDiagnostics",
        "diagnostics",
        "lastRunError",
        "error",
    };

    /// <summary>
    /// Redacts a single command/agent output summary or diagnostic string for external delivery.
    /// Returns <paramref name="summary"/> unchanged when it is null. Apply this to every free-text
    /// field that would cross the external-delivery boundary.
    /// </summary>
    /// <param name="redactor">The secret redactor (the gateway <c>SecretRedactor</c> at runtime).</param>
    /// <param name="summary">The full, unredacted summary/diagnostic text, or null.</param>
    public static string? RedactSummary(ISecretRedactor redactor, string? summary)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        return summary is null ? null : redactor.RedactForExternalDelivery(summary);
    }

    /// <summary>
    /// Produces an external-delivery payload for <paramref name="job"/>: an <c>ExternalJob</c> whose
    /// free-text summary fields are redacted and whose diagnostic metadata is stripped, alongside the
    /// original <c>LocalJob</c> kept intact for the local operator record. Callers deliver
    /// <see cref="ExternalDeliveryPayload.ExternalJob"/> and persist/log
    /// <see cref="ExternalDeliveryPayload.LocalJob"/>.
    /// </summary>
    /// <param name="redactor">The secret redactor (the gateway <c>SecretRedactor</c> at runtime).</param>
    /// <param name="job">The full, unredacted cron job as held locally.</param>
    public static ExternalDeliveryPayload PrepareForExternalDelivery(ISecretRedactor redactor, CronJob job)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(job);

        var external = job with
        {
            Message = job.Message is null ? null : redactor.RedactForExternalDelivery(job.Message),
            ShellCommand = job.ShellCommand is null ? null : redactor.RedactForExternalDelivery(job.ShellCommand),
            LastRunError = null, // never externalise raw run errors
            Metadata = StripDiagnostics(job.Metadata),
        };

        return new ExternalDeliveryPayload(LocalJob: job, ExternalJob: external);
    }

    /// <summary>
    /// Returns a copy of <paramref name="metadata"/> with all diagnostic keys removed, or null when
    /// the input is null. Non-diagnostic operational keys (e.g. "timeoutSeconds") are preserved.
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? StripDiagnostics(
        IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return null;

        var cleaned = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in metadata)
        {
            if (IsDiagnosticKey(key))
                continue;
            cleaned[key] = value;
        }

        return cleaned;
    }

    private static bool IsDiagnosticKey(string key)
    {
        foreach (var known in DiagnosticMetadataKeys)
        {
            if (string.Equals(key, known, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Any key mentioning "diagnostic" is treated as diagnostic material defensively.
        return key.Contains("diagnostic", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Paired result of <see cref="CronExternalDeliveryRedactor.PrepareForExternalDelivery"/>:
/// <see cref="LocalJob"/> is the full unredacted original for the operator record;
/// <see cref="ExternalJob"/> is the redacted, diagnostics-stripped copy safe to deliver externally.
/// </summary>
public sealed record ExternalDeliveryPayload(CronJob LocalJob, CronJob ExternalJob);
