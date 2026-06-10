namespace BotNexus.Gateway.Isolation;

/// <summary>
/// No-op implementation of <see cref="IDockerSandboxRunner"/> used when Docker is
/// not configured or available. Always reports Docker as unavailable, causing the
/// <see cref="DockerSandboxIsolationStrategy"/> to throw a descriptive error if
/// any agent is configured to use the "docker-sandbox" isolation strategy.
/// </summary>
internal sealed class NullDockerSandboxRunner : IDockerSandboxRunner
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task CreateAsync(string name, ResolvedDockerSandboxOptions options, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Docker sandbox is not available. NullDockerSandboxRunner is active — " +
            "Docker is not installed or the sandbox feature is not enabled.");

    public Task StopAsync(string name, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> IsHealthyAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task CopyToSandboxAsync(string name, string hostPath, string sandboxPath, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Docker sandbox is not available. Cannot copy files to sandbox.");

    public Task CopyFromSandboxAsync(string name, string sandboxPath, string hostPath, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Docker sandbox is not available. Cannot copy files from sandbox.");
}
