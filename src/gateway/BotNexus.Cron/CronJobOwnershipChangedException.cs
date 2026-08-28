using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

/// <summary>
/// Thrown when a cron definition commit is rejected because the job's ownership changed between
/// the caller's authorization decision and the write (#3573).
/// </summary>
/// <remarks>
/// It derives from <see cref="UnauthorizedAccessException"/> deliberately: this IS an authorization
/// failure, just one detected at commit rather than at read, and the model-facing <c>cron</c> tool
/// already surfaces <see cref="UnauthorizedAccessException"/> for the read-time refusal. Callers
/// that need to distinguish "you never had rights" from "you lost them mid-flight" - the REST seam
/// wants a specific 409-shaped answer - can catch this type; everything else keeps treating it as
/// the authorization failure it is. It is emphatically NOT a <see cref="KeyNotFoundException"/>:
/// the job exists, the caller simply no longer owns it.
/// </remarks>
public sealed class CronJobOwnershipChangedException : UnauthorizedAccessException
{
    /// <summary>Creates the exception for <paramref name="jobId"/>.</summary>
    public CronJobOwnershipChangedException(JobId jobId)
        : base($"Cron job '{jobId.Value}' changed ownership while the update was in flight; the update was rejected.")
        => JobId = jobId;

    /// <summary>The job whose commit was rejected.</summary>
    public JobId JobId { get; }
}
