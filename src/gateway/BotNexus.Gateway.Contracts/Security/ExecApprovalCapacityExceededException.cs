namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// Thrown by <see cref="IExecApprovalManager.Issue"/> when the pending-approval registry is
/// already at its configured maximum and no expired entry can be reclaimed.
/// </summary>
/// <remarks>
/// The pending registry is bounded so that approvals which are never answered cannot retain their
/// decoded command payloads for the lifetime of the process (issue #2746). Refusing at issue time
/// is deliberate: an unbounded registry would silently accumulate sensitive command text.
/// </remarks>
public sealed class ExecApprovalCapacityExceededException : InvalidOperationException
{
    /// <summary>Creates the exception with the configured maximum for diagnostics.</summary>
    /// <param name="maxPending">The configured maximum number of pending approvals.</param>
    public ExecApprovalCapacityExceededException(int maxPending)
        : base($"Cannot issue an exec approval: the pending-approval registry is full ({maxPending} entries).")
        => MaxPending = maxPending;

    /// <summary>The configured maximum number of concurrently pending approvals.</summary>
    public int MaxPending { get; }
}
