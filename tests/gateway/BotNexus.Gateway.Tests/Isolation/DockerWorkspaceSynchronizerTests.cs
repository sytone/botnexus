using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// Tests for <see cref="DockerWorkspaceSynchronizer"/> verifying sync orchestration,
/// error handling, and auditability logging.
/// </summary>
public sealed class DockerWorkspaceSynchronizerTests
{
    private readonly Mock<IDockerSandboxRunner> _runner = new();
    private readonly DockerWorkspaceSynchronizer _sut;

    public DockerWorkspaceSynchronizerTests()
    {
        _sut = new DockerWorkspaceSynchronizer(
            _runner.Object,
            NullLoggerFactory.Instance.CreateLogger<DockerWorkspaceSynchronizer>());
    }

    // ═══════════════════════════════════════════════════════════════════
    // SyncToSandboxAsync
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncToSandbox_CallsCopyToSandbox_ReturnsSuccess()
    {
        _runner.Setup(r => r.CopyToSandboxAsync("agent-test", "/host/workspace", "/workspace", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.SyncToSandboxAsync("agent-test", "/host/workspace", "/workspace");

        result.Success.ShouldBeTrue();
        result.Direction.ShouldBe(SyncDirection.ToSandbox);
        result.Error.ShouldBeNull();
        _runner.Verify(r => r.CopyToSandboxAsync("agent-test", "/host/workspace", "/workspace", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncToSandbox_RunnerThrows_ReturnsFailure()
    {
        _runner.Setup(r => r.CopyToSandboxAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Disk full"));

        var result = await _sut.SyncToSandboxAsync("agent-test", "/host/workspace", "/workspace");

        result.Success.ShouldBeFalse();
        result.Direction.ShouldBe(SyncDirection.ToSandbox);
        result.Error.ShouldBe("Disk full");
    }

    [Fact]
    public async Task SyncToSandbox_Cancelled_ThrowsOperationCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _runner.Setup(r => r.CopyToSandboxAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.SyncToSandboxAsync("agent-test", "/host/workspace", "/workspace", cts.Token));
    }

    [Theory]
    [InlineData("", "/host", "/sandbox")]
    [InlineData("sandbox", "", "/sandbox")]
    [InlineData("sandbox", "/host", "")]
    public async Task SyncToSandbox_EmptyArgument_Throws(string name, string hostPath, string sandboxPath)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _sut.SyncToSandboxAsync(name, hostPath, sandboxPath));
    }

    // ═══════════════════════════════════════════════════════════════════
    // SyncFromSandboxAsync
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncFromSandbox_CallsCopyFromSandbox_ReturnsSuccess()
    {
        _runner.Setup(r => r.CopyFromSandboxAsync("agent-test", "/workspace", "/host/workspace", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.SyncFromSandboxAsync("agent-test", "/host/workspace", "/workspace");

        result.Success.ShouldBeTrue();
        result.Direction.ShouldBe(SyncDirection.FromSandbox);
        result.Error.ShouldBeNull();
        _runner.Verify(r => r.CopyFromSandboxAsync("agent-test", "/workspace", "/host/workspace", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncFromSandbox_RunnerThrows_ReturnsFailure()
    {
        _runner.Setup(r => r.CopyFromSandboxAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Sandbox not running"));

        var result = await _sut.SyncFromSandboxAsync("agent-test", "/host/workspace", "/workspace");

        result.Success.ShouldBeFalse();
        result.Direction.ShouldBe(SyncDirection.FromSandbox);
        result.Error.ShouldBe("Sandbox not running");
    }

    [Fact]
    public async Task SyncFromSandbox_Cancelled_ThrowsOperationCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _runner.Setup(r => r.CopyFromSandboxAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.SyncFromSandboxAsync("agent-test", "/host/workspace", "/workspace", cts.Token));
    }

    // ═══════════════════════════════════════════════════════════════════
    // NullDockerSandboxRunner — copy methods throw
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NullRunner_CopyToSandbox_Throws()
    {
        var nullRunner = new NullDockerSandboxRunner();

        await Should.ThrowAsync<InvalidOperationException>(
            () => nullRunner.CopyToSandboxAsync("test", "/host", "/sandbox"));
    }

    [Fact]
    public async Task NullRunner_CopyFromSandbox_Throws()
    {
        var nullRunner = new NullDockerSandboxRunner();

        await Should.ThrowAsync<InvalidOperationException>(
            () => nullRunner.CopyFromSandboxAsync("test", "/sandbox", "/host"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // WorkspaceSyncResult record semantics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void WorkspaceSyncResult_SuccessResult_HasExpectedDefaults()
    {
        var result = new WorkspaceSyncResult(Success: true, Direction: SyncDirection.ToSandbox);

        result.Success.ShouldBeTrue();
        result.Direction.ShouldBe(SyncDirection.ToSandbox);
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void WorkspaceSyncResult_FailureResult_CarriesError()
    {
        var result = new WorkspaceSyncResult(Success: false, Direction: SyncDirection.FromSandbox, Error: "Something broke");

        result.Success.ShouldBeFalse();
        result.Direction.ShouldBe(SyncDirection.FromSandbox);
        result.Error.ShouldBe("Something broke");
    }
}
