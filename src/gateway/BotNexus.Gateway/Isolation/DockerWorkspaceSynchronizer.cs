using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Default implementation of <see cref="IWorkspaceSynchronizer"/> that delegates
/// file copy operations to <see cref="IDockerSandboxRunner"/>. All operations
/// are logged for auditability.
/// </summary>
/// <remarks>
/// MVP: full directory copy both directions. Writes inside the sandbox are contained
/// until the explicit SyncFromSandbox call copies them back to the host.
/// </remarks>
public sealed class DockerWorkspaceSynchronizer : IWorkspaceSynchronizer
{
    private readonly IDockerSandboxRunner _runner;
    private readonly ILogger<DockerWorkspaceSynchronizer> _logger;

    public DockerWorkspaceSynchronizer(
        IDockerSandboxRunner runner,
        ILogger<DockerWorkspaceSynchronizer> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<WorkspaceSyncResult> SyncToSandboxAsync(
        string sandboxName,
        string hostWorkspacePath,
        string sandboxWorkspacePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxName);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostWorkspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxWorkspacePath);

        _logger.LogInformation(
            "Workspace sync: copying host '{HostPath}' → sandbox '{SandboxName}:{SandboxPath}'",
            hostWorkspacePath, sandboxName, sandboxWorkspacePath);

        try
        {
            await _runner.CopyToSandboxAsync(
                sandboxName, hostWorkspacePath, sandboxWorkspacePath, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Workspace sync: host → sandbox complete for '{SandboxName}'",
                sandboxName);

            return new WorkspaceSyncResult(Success: true, Direction: SyncDirection.ToSandbox);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Workspace sync: host → sandbox FAILED for '{SandboxName}': {Message}",
                sandboxName, ex.Message);

            return new WorkspaceSyncResult(
                Success: false,
                Direction: SyncDirection.ToSandbox,
                Error: ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceSyncResult> SyncFromSandboxAsync(
        string sandboxName,
        string hostWorkspacePath,
        string sandboxWorkspacePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxName);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostWorkspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxWorkspacePath);

        _logger.LogInformation(
            "Workspace sync: copying sandbox '{SandboxName}:{SandboxPath}' → host '{HostPath}'",
            sandboxName, sandboxWorkspacePath, hostWorkspacePath);

        try
        {
            await _runner.CopyFromSandboxAsync(
                sandboxName, sandboxWorkspacePath, hostWorkspacePath, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Workspace sync: sandbox → host complete for '{SandboxName}'",
                sandboxName);

            return new WorkspaceSyncResult(Success: true, Direction: SyncDirection.FromSandbox);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Workspace sync: sandbox → host FAILED for '{SandboxName}': {Message}",
                sandboxName, ex.Message);

            return new WorkspaceSyncResult(
                Success: false,
                Direction: SyncDirection.FromSandbox,
                Error: ex.Message);
        }
    }
}
