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
}
