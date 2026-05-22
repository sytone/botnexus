using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Tests for <see cref="DefaultSubAgentManager"/> file access path merging (#236).
/// </summary>
public sealed class SubAgentFileAccessTests
{
    // ── MergeFileAccess: no additional paths ──────────────────────────────────

    [Fact]
    public async Task SpawnAsync_WithNoAdditionalPaths_InheritsBaseDescriptorFileAccess()
    {
        var parentFileAccess = new FileAccessPolicy
        {
            AllowedReadPaths = ["/parent/workspace"],
            AllowedWritePaths = ["/parent/workspace"]
        };

        AgentDescriptor? registered = null;
        var (manager, _) = CreateManagerWithCapture(
            parentAccess: parentFileAccess,
            onRegister: d => registered = d);

        await manager.SpawnAsync(CreateSpawnRequest());

        registered.ShouldNotBeNull();
        registered!.FileAccess.ShouldNotBeNull();
        registered.FileAccess!.AllowedReadPaths.ShouldBe(["/parent/workspace"]);
        registered.FileAccess.AllowedWritePaths.ShouldBe(["/parent/workspace"]);
    }

    // ── MergeFileAccess: additional read paths within parent's grant ─────────

    [Fact]
    public async Task SpawnAsync_WithAdditionalReadPaths_MergesGrantedPaths()
    {
        var parentFileAccess = new FileAccessPolicy
        {
            AllowedReadPaths = ["/repos/project", "/parent/workspace"],
            AllowedWritePaths = ["/parent/workspace"]
        };

        AgentDescriptor? registered = null;
        var (manager, _) = CreateManagerWithCapture(
            parentAccess: parentFileAccess,
            onRegister: d => registered = d);

        await manager.SpawnAsync(CreateSpawnRequest(
            additionalReadPaths: ["/repos/project/src"]));

        registered.ShouldNotBeNull();
        registered!.FileAccess.ShouldNotBeNull();
        registered.FileAccess!.AllowedReadPaths.ShouldContain("/repos/project/src");
        registered.FileAccess.AllowedReadPaths.ShouldContain("/parent/workspace"); // base preserved
    }

    // ── MergeFileAccess: privilege confinement ────────────────────────────────

    [Fact]
    public async Task SpawnAsync_WithPathsOutsideParentGrant_FiltersThemOut()
    {
        // Parent can only read /parent/workspace. Sub-agent requests /etc/secrets -- must be filtered.
        var parentFileAccess = new FileAccessPolicy
        {
            AllowedReadPaths = ["/parent/workspace"],
            AllowedWritePaths = ["/parent/workspace"]
        };

        AgentDescriptor? registered = null;
        var (manager, _) = CreateManagerWithCapture(
            parentAccess: parentFileAccess,
            onRegister: d => registered = d);

        await manager.SpawnAsync(CreateSpawnRequest(
            additionalReadPaths: ["/etc/secrets", "/parent/workspace/sub"]));

        registered.ShouldNotBeNull();
        registered!.FileAccess.ShouldNotBeNull();
        // /etc/secrets must NOT appear -- outside parent grant
        registered.FileAccess!.AllowedReadPaths.ShouldNotContain("/etc/secrets");
        // /parent/workspace/sub IS within parent grant
        registered.FileAccess.AllowedReadPaths.ShouldContain("/parent/workspace/sub");
    }

    // ── MergeFileAccess: parent with no restrictions allows any path ──────────

    [Fact]
    public async Task SpawnAsync_WithNullParentFileAccess_AllowsAnyAdditionalPath()
    {
        // Parent descriptor has no FileAccess policy (no restrictions). All paths should be granted.
        AgentDescriptor? registered = null;
        var (manager, _) = CreateManagerWithCapture(
            parentAccess: null,
            onRegister: d => registered = d);

        await manager.SpawnAsync(CreateSpawnRequest(
            additionalReadPaths: ["/any/path"],
            additionalWritePaths: ["/another/path"]));

        registered.ShouldNotBeNull();
        registered!.FileAccess.ShouldNotBeNull();
        registered.FileAccess!.AllowedReadPaths.ShouldContain("/any/path");
        registered.FileAccess.AllowedWritePaths.ShouldContain("/another/path");
    }

    // ── MergeFileAccess: write paths ─────────────────────────────────────────

    [Fact]
    public async Task SpawnAsync_WithAdditionalWritePaths_MergesIntoWriteGrant()
    {
        var parentFileAccess = new FileAccessPolicy
        {
            AllowedReadPaths = ["/parent/workspace"],
            AllowedWritePaths = ["/parent/workspace", "/shared/output"]
        };

        AgentDescriptor? registered = null;
        var (manager, _) = CreateManagerWithCapture(
            parentAccess: parentFileAccess,
            onRegister: d => registered = d);

        await manager.SpawnAsync(CreateSpawnRequest(
            additionalWritePaths: ["/shared/output/report.txt"]));

        registered.ShouldNotBeNull();
        registered!.FileAccess.ShouldNotBeNull();
        registered.FileAccess!.AllowedWritePaths.ShouldContain("/shared/output/report.txt");
        registered.FileAccess.AllowedWritePaths.ShouldContain("/parent/workspace"); // base preserved
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SubAgentSpawnRequest CreateSpawnRequest(
        IReadOnlyList<string>? additionalReadPaths = null,
        IReadOnlyList<string>? additionalWritePaths = null) => new()
    {
        ParentAgentId = AgentId.From("parent-agent"),
        ParentSessionId = SessionId.From("parent-session"),
        Task = "File access test task",
        AdditionalReadPaths = additionalReadPaths ?? [],
        AdditionalWritePaths = additionalWritePaths ?? []
    };

    private static (DefaultSubAgentManager manager, Mock<IAgentRegistry> registry) CreateManagerWithCapture(
        FileAccessPolicy? parentAccess,
        Action<AgentDescriptor> onRegister)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session"));
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(AgentId.From("parent-agent")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("parent-agent"),
                DisplayName = "Parent",
                ModelId = "gpt-4o",
                ApiProvider = "openai",
                FileAccess = parentAccess
            });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry
            .Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
            .Callback<AgentDescriptor>(onRegister);

        var options = new TestOptionsMonitor<GatewayOptions>(new GatewayOptions());

        var manager = new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            new Mock<IActivityBroadcaster>().Object,
            new Mock<IChannelDispatcher>().Object,
            options,
            new Mock<Microsoft.Extensions.Logging.ILogger<DefaultSubAgentManager>>().Object);

        return (manager, registry);
    }
}
