using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Routes tool execution commands into a running Docker sandbox.
/// Provides the bridge between gateway tool calls and sandboxed execution,
/// handling command dispatch, environment setup, and timeout enforcement.
/// </summary>
/// <remarks>
/// Tool calls that require file I/O or shell execution are routed through this
/// class to execute inside the sandbox container rather than on the host. The
/// gateway maintains control over which commands are allowed and enforces resource
/// limits via the <see cref="IDockerSandboxRunner.ExecAsync"/> contract.
/// </remarks>
public sealed class DockerSandboxToolRouter
{
    private readonly IDockerSandboxRunner _runner;
    private readonly ILogger<DockerSandboxToolRouter> _logger;

    public DockerSandboxToolRouter(
        IDockerSandboxRunner runner,
        ILogger<DockerSandboxToolRouter> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes a command inside the specified Docker sandbox.
    /// </summary>
    /// <param name="sandboxName">Name of the target sandbox container.</param>
    /// <param name="command">Command to execute inside the sandbox.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="workingDirectory">Optional working directory inside the sandbox.</param>
    /// <param name="environmentVariables">Optional environment variables for the command.</param>
    /// <param name="timeoutSeconds">Optional timeout in seconds. Null = no timeout.</param>
    /// <returns>Execution result with exit code, stdout, and stderr.</returns>
    public async Task<SandboxExecResult> ExecInSandboxAsync(
        string sandboxName,
        string command,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        Dictionary<string, string>? environmentVariables = null,
        int? timeoutSeconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        _logger.LogDebug(
            "Routing tool execution to sandbox '{SandboxName}': {Command} (workDir: {WorkDir}, timeout: {Timeout}s)",
            sandboxName, command, workingDirectory ?? "(default)", timeoutSeconds?.ToString() ?? "none");

        var result = await _runner.ExecAsync(
            sandboxName,
            command,
            workingDirectory,
            environmentVariables,
            timeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            _logger.LogWarning(
                "Sandbox exec in '{SandboxName}' exited with code {ExitCode}: {Stderr}",
                sandboxName, result.ExitCode, result.Stderr.Length > 200 ? result.Stderr[..200] + "..." : result.Stderr);
        }
        else
        {
            _logger.LogDebug(
                "Sandbox exec in '{SandboxName}' completed successfully ({StdoutLen} bytes stdout)",
                sandboxName, result.Stdout.Length);
        }

        return result;
    }
}
