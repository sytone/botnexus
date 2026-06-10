using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Gateway.Tests.Isolation;

public class DockerSandboxToolRouterTests
{
    private readonly FakeDockerSandboxRunner _runner = new();
    private readonly DockerSandboxToolRouter _router;
    private const string SandboxName = "agent-test";

    public DockerSandboxToolRouterTests()
    {
        _router = new DockerSandboxToolRouter(
            _runner,
            NullLogger<DockerSandboxToolRouter>.Instance);
    }

    [Fact]
    public async Task ExecInSandboxAsync_ExecutesCommandAndReturnsOutput()
    {
        _runner.ExecResults["echo hello"] = new SandboxExecResult(0, "hello\n", "");

        var result = await _router.ExecInSandboxAsync(
            SandboxName, "echo hello", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\n", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task ExecInSandboxAsync_PropagatesNonZeroExitCode()
    {
        _runner.ExecResults["bad-command"] = new SandboxExecResult(127, "", "command not found");

        var result = await _router.ExecInSandboxAsync(
            SandboxName, "bad-command", CancellationToken.None);

        Assert.Equal(127, result.ExitCode);
        Assert.Equal("command not found", result.Stderr);
    }

    [Fact]
    public async Task ExecInSandboxAsync_ThrowsOnNullSandboxName()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _router.ExecInSandboxAsync(null!, "echo", CancellationToken.None));
    }

    [Fact]
    public async Task ExecInSandboxAsync_ThrowsOnNullCommand()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _router.ExecInSandboxAsync(SandboxName, null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExecInSandboxAsync_PassesEnvironmentVariables()
    {
        var env = new Dictionary<string, string> { ["MY_VAR"] = "value1" };
        _runner.ExecResults["env-test"] = new SandboxExecResult(0, "MY_VAR=value1", "");

        var result = await _router.ExecInSandboxAsync(
            SandboxName, "env-test", CancellationToken.None, environmentVariables: env);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("MY_VAR", _runner.LastEnvironmentVariables!.Keys);
    }

    [Fact]
    public async Task ExecInSandboxAsync_RespectsWorkingDirectory()
    {
        _runner.ExecResults["pwd"] = new SandboxExecResult(0, "/workspace\n", "");

        var result = await _router.ExecInSandboxAsync(
            SandboxName, "pwd", CancellationToken.None, workingDirectory: "/workspace");

        Assert.Equal("/workspace", _runner.LastWorkingDirectory);
    }

    [Fact]
    public async Task ExecInSandboxAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _router.ExecInSandboxAsync(SandboxName, "sleep 100", cts.Token));
    }

    [Fact]
    public async Task ExecInSandboxAsync_EnforcesTimeout()
    {
        _runner.SimulateTimeout = true;

        var result = await _router.ExecInSandboxAsync(
            SandboxName, "long-running", CancellationToken.None,
            timeoutSeconds: 5);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("timeout", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeDockerSandboxRunner : IDockerSandboxRunner
    {
        public Dictionary<string, SandboxExecResult> ExecResults { get; } = new();
        public Dictionary<string, string>? LastEnvironmentVariables { get; set; }
        public string? LastWorkingDirectory { get; set; }
        public bool SimulateTimeout { get; set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task CreateAsync(string name, ResolvedDockerSandboxOptions options, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync(string name, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> IsHealthyAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task CopyToSandboxAsync(string name, string hostPath, string sandboxPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CopyFromSandboxAsync(string name, string sandboxPath, string hostPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SandboxExecResult> ExecAsync(
            string name,
            string command,
            string? workingDirectory = null,
            Dictionary<string, string>? environmentVariables = null,
            int? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastWorkingDirectory = workingDirectory;
            LastEnvironmentVariables = environmentVariables;

            if (SimulateTimeout)
                return Task.FromResult(new SandboxExecResult(-1, "", "Process killed: execution timeout exceeded"));

            if (ExecResults.TryGetValue(command, out var result))
                return Task.FromResult(result);

            return Task.FromResult(new SandboxExecResult(127, "", $"command not found: {command}"));
        }
    }
}
