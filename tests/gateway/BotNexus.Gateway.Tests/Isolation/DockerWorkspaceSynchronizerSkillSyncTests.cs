using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// Tests for <see cref="DockerWorkspaceSynchronizer.SyncSkillsToSandboxAsync"/>.
/// </summary>
public sealed class DockerWorkspaceSynchronizerSkillSyncTests
{
    [Fact]
    public async Task SyncSkillsToSandbox_CallsCopyToSandboxWithCorrectPaths()
    {
        var runner = new Mock<IDockerSandboxRunner>();
        runner.Setup(r => r.CopyToSandboxAsync(
            "agent-farnsworth", "/host/skills", "/workspace/skills", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var synchronizer = new DockerWorkspaceSynchronizer(
            runner.Object,
            NullLogger<DockerWorkspaceSynchronizer>.Instance);

        var result = await synchronizer.SyncSkillsToSandboxAsync(
            "agent-farnsworth", "/host/skills", "/workspace/skills");

        Assert.True(result.Success);
        Assert.Equal(SyncDirection.ToSandbox, result.Direction);
        runner.Verify(r => r.CopyToSandboxAsync(
            "agent-farnsworth", "/host/skills", "/workspace/skills", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncSkillsToSandbox_ReturnsFailureOnException()
    {
        var runner = new Mock<IDockerSandboxRunner>();
        runner.Setup(r => r.CopyToSandboxAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Docker not running"));

        var synchronizer = new DockerWorkspaceSynchronizer(
            runner.Object,
            NullLogger<DockerWorkspaceSynchronizer>.Instance);

        var result = await synchronizer.SyncSkillsToSandboxAsync(
            "agent-test", "/host/skills", "/workspace/skills");

        Assert.False(result.Success);
        Assert.Equal(SyncDirection.ToSandbox, result.Direction);
        Assert.Contains("Docker not running", result.Error);
    }

    [Theory]
    [InlineData("", "/host/skills", "/sandbox/skills")]
    [InlineData("sandbox", "", "/sandbox/skills")]
    [InlineData("sandbox", "/host/skills", "")]
    public async Task SyncSkillsToSandbox_ThrowsOnNullOrWhitespaceArguments(
        string sandboxName, string hostDir, string sandboxPath)
    {
        var runner = new Mock<IDockerSandboxRunner>();
        var synchronizer = new DockerWorkspaceSynchronizer(
            runner.Object,
            NullLogger<DockerWorkspaceSynchronizer>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            () => synchronizer.SyncSkillsToSandboxAsync(sandboxName, hostDir, sandboxPath));
    }
}
