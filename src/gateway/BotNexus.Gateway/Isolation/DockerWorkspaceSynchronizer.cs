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

    /// <summary>
    /// Copies skill scripts from the host skills directory into the sandbox.
    /// This ensures skill references in the agent prompt resolve correctly inside the container.
    /// </summary>
    /// <param name="sandboxName">Target sandbox name.</param>
    /// <param name="hostSkillsDir">Absolute path to the host skills directory.</param>
    /// <param name="sandboxSkillsPath">Path inside the sandbox to copy skills into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result describing what was synced.</returns>
    public async Task<WorkspaceSyncResult> SyncSkillsToSandboxAsync(
        string sandboxName,
        string hostSkillsDir,
        string sandboxSkillsPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxName);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostSkillsDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxSkillsPath);

        _logger.LogInformation(
            "Skills sync: copying host '{HostSkillsDir}' → sandbox '{SandboxName}:{SandboxSkillsPath}'",
            hostSkillsDir, sandboxName, sandboxSkillsPath);

        try
        {
            await _runner.CopyToSandboxAsync(
                sandboxName, hostSkillsDir, sandboxSkillsPath, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Skills sync: host → sandbox complete for '{SandboxName}'",
                sandboxName);

            return new WorkspaceSyncResult(Success: true, Direction: SyncDirection.ToSandbox);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Skills sync: host → sandbox FAILED for '{SandboxName}': {Message}",
                sandboxName, ex.Message);

            return new WorkspaceSyncResult(
                Success: false,
                Direction: SyncDirection.ToSandbox,
                Error: ex.Message);
        }
    }
}
