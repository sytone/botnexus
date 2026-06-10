namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Abstraction over Docker sandbox CLI operations, enabling testability
/// without requiring a real Docker installation.
/// </summary>
public interface IDockerSandboxRunner
{
    /// <summary>
    /// Checks whether the Docker sandbox runtime is available on this host.
    /// Returns false if Docker is not installed or the sandbox command is not available.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new Docker sandbox with the given name and resolved configuration.
    /// Equivalent to <c>docker sandbox create --name {name} [--image ...] [--memory ...] [--network ...]</c>.
    /// </summary>
    /// <param name="name">The sandbox name (e.g., "agent-farnsworth").</param>
    /// <param name="options">Resolved per-agent sandbox configuration (image, network, memory).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateAsync(string name, ResolvedDockerSandboxOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops and removes a running Docker sandbox.
    /// Equivalent to <c>docker sandbox stop {name}</c>.
    /// </summary>
    /// <param name="name">The sandbox name to stop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StopAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a sandbox is running and healthy.
    /// </summary>
    /// <param name="name">The sandbox name to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the sandbox is running and healthy.</returns>
    Task<bool> IsHealthyAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies files from the host filesystem into the sandbox.
    /// Equivalent to <c>docker sandbox cp {hostPath}/. {name}:{sandboxPath}</c>.
    /// </summary>
    /// <param name="name">The sandbox name.</param>
    /// <param name="hostPath">Source path on the host (directory).</param>
    /// <param name="sandboxPath">Destination path inside the sandbox.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CopyToSandboxAsync(string name, string hostPath, string sandboxPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies files from the sandbox back to the host filesystem.
    /// Equivalent to <c>docker sandbox cp {name}:{sandboxPath}/. {hostPath}</c>.
    /// </summary>
    /// <param name="name">The sandbox name.</param>
    /// <param name="sandboxPath">Source path inside the sandbox.</param>
    /// <param name="hostPath">Destination path on the host (directory).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CopyFromSandboxAsync(string name, string sandboxPath, string hostPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a command inside a running Docker sandbox.
    /// Equivalent to <c>docker sandbox exec {name} -- {command}</c>.
    /// </summary>
    /// <param name="name">The sandbox name to execute in.</param>
    /// <param name="command">The command string to execute.</param>
    /// <param name="workingDirectory">Optional working directory inside the sandbox.</param>
    /// <param name="environmentVariables">Optional environment variables to set for the command.</param>
    /// <param name="timeoutSeconds">Optional timeout in seconds. Null = no timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result including exit code, stdout, and stderr.</returns>
    Task<SandboxExecResult> ExecAsync(
        string name,
        string command,
        string? workingDirectory = null,
        Dictionary<string, string>? environmentVariables = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default);
}
