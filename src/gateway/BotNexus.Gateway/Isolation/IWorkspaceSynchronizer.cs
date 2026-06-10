using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Orchestrates workspace file synchronization between the host filesystem and a
/// Docker sandbox. Copies workspace contents into the sandbox before agent dispatch
/// and copies any modifications back to the host after dispatch completes.
/// </summary>
/// <remarks>
/// Phase 1 (MVP): full-copy both directions. Incremental sync is a future optimization.
/// Sync operations are auditable — all copy operations are logged at Information level.
/// </remarks>
public interface IWorkspaceSynchronizer
{
    /// <summary>
    /// Copies the agent workspace from the host into the sandbox.
    /// Called before agent dispatch.
    /// </summary>
    /// <param name="sandboxName">Target sandbox name.</param>
    /// <param name="hostWorkspacePath">Absolute path to the agent workspace on the host.</param>
    /// <param name="sandboxWorkspacePath">Path inside the sandbox to copy into (e.g., "/workspace").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result describing what was synced.</returns>
    Task<WorkspaceSyncResult> SyncToSandboxAsync(
        string sandboxName,
        string hostWorkspacePath,
        string sandboxWorkspacePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies any workspace modifications from the sandbox back to the host.
    /// Called after agent dispatch completes.
    /// </summary>
    /// <param name="sandboxName">Source sandbox name.</param>
    /// <param name="hostWorkspacePath">Absolute path to the agent workspace on the host.</param>
    /// <param name="sandboxWorkspacePath">Path inside the sandbox to copy from (e.g., "/workspace").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result describing what was synced.</returns>
    Task<WorkspaceSyncResult> SyncFromSandboxAsync(
        string sandboxName,
        string hostWorkspacePath,
        string sandboxWorkspacePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a workspace sync operation.
/// </summary>
/// <param name="Success">Whether the sync completed without errors.</param>
/// <param name="Direction">Direction of the sync (ToSandbox or FromSandbox).</param>
/// <param name="Error">Error message if the sync failed, null otherwise.</param>
public sealed record WorkspaceSyncResult(
    bool Success,
    SyncDirection Direction,
    string? Error = null);

/// <summary>Direction of workspace synchronization.</summary>
public enum SyncDirection
{
    /// <summary>Host → Sandbox (before dispatch).</summary>
    ToSandbox,

    /// <summary>Sandbox → Host (after dispatch).</summary>
    FromSandbox
}
