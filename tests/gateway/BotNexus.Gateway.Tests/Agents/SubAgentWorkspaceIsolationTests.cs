using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Tests for sub-agent workspace isolation: temp workspace provisioning,
/// shareWorkspace, and grantedPaths privilege confinement (issue #466).
/// </summary>
public sealed class SubAgentWorkspaceIsolationTests
{
    private static readonly AgentId ParentAgentId = AgentId.From("parent-agent");
    private static readonly SessionId ParentSessionId = SessionId.From("parent-session");

    [Fact]
    public async Task SpawnAsync_ProvisionsTempWorkspace_WhenWorkspaceManagerPresent()
    {
        // Verify that SpawnAsync calls ProvisionWorkspace on the workspace manager for the child agent.
        var workspaceManagerMock = new Mock<IAgentWorkspaceManager>();
        workspaceManagerMock
            .Setup(wm => wm.ProvisionWorkspace(It.IsAny<string>()))
            .Returns((string agentName) => Path.Combine(Path.GetTempPath(), "botnexus-subagent-workspaces", agentName, "workspace"));
        workspaceManagerMock
            .Setup(wm => wm.GetWorkspacePath(It.IsAny<string>()))
            .Returns((string agentName) => Path.Combine(Path.GetTempPath(), "botnexus-subagent-workspaces", agentName, "workspace"));

        var (manager, _) = CreateManager(workspaceManager: workspaceManagerMock.Object);

        await manager.SpawnAsync(CreateSpawnRequest());

        workspaceManagerMock.Verify(
            wm => wm.ProvisionWorkspace(It.Is<string>(s => s.Contains("--subagent--", StringComparison.Ordinal))),
            Times.Once,
            "ProvisionWorkspace should be called exactly once for the child agent");
    }

    [Fact]
    public async Task SpawnAsync_NoWorkspaceManager_DoesNotThrow()
    {
        // No IAgentWorkspaceManager registered — should still spawn successfully.
        var (manager, _) = CreateManager(workspaceManager: null);

        var info = await manager.SpawnAsync(CreateSpawnRequest());

        info.ShouldNotBeNull();
        info.Status.ShouldBe(SubAgentStatus.Running);
    }

    [Fact]
    public async Task SpawnAsync_ShareParentWorkspace_True_IncludesParentWorkspaceInFileAccess()
    {
        var fileSystem = new MockFileSystem();
        var homePath = "/botnexus-home";
        var workspaceManager = new FileAgentWorkspaceManager(
            new BotNexus.Gateway.Configuration.BotNexusHome(fileSystem, homePath),
            fileSystem);

        AgentDescriptor? capturedDescriptor = null;
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(ParentAgentId)).Returns(MakeParentDescriptor());
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
            .Callback<AgentDescriptor>(d => capturedDescriptor = d);

        var (manager, _) = CreateManager(
            workspaceManager: workspaceManager,
            registry: registry);

        await manager.SpawnAsync(CreateSpawnRequest(shareParentWorkspace: true));

        capturedDescriptor.ShouldNotBeNull();
        var parentWorkspacePath = workspaceManager.GetWorkspacePath(ParentAgentId.Value);
        capturedDescriptor!.FileAccess.ShouldNotBeNull();
        capturedDescriptor.FileAccess!.AllowedReadPaths
            .ShouldContain(p => p.Equals(parentWorkspacePath, StringComparison.OrdinalIgnoreCase),
                "parent workspace path should be in child read grants");
        capturedDescriptor.FileAccess.AllowedWritePaths
            .ShouldContain(p => p.Equals(parentWorkspacePath, StringComparison.OrdinalIgnoreCase),
                "parent workspace path should be in child write grants");
    }

    [Fact]
    public async Task SpawnAsync_GrantedPaths_WithinParentGrant_AreIncluded()
    {
        // Parent has unrestricted access (FileAccess == null) — all granted paths should pass through.
        var fileSystem = new MockFileSystem();
        var homePath = "/botnexus-home";
        var workspaceManager = new FileAgentWorkspaceManager(
            new BotNexus.Gateway.Configuration.BotNexusHome(fileSystem, homePath),
            fileSystem);
        var grantedPath = Path.GetFullPath("/shared/repo");

        AgentDescriptor? capturedDescriptor = null;
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(ParentAgentId)).Returns(MakeParentDescriptor(fileAccess: null));
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
            .Callback<AgentDescriptor>(d => capturedDescriptor = d);

        var (manager, _) = CreateManager(workspaceManager: workspaceManager, registry: registry);

        await manager.SpawnAsync(CreateSpawnRequest(grantedPaths: [grantedPath]));

        capturedDescriptor.ShouldNotBeNull();
        capturedDescriptor!.FileAccess!.AllowedReadPaths
            .ShouldContain(p => p.Equals(grantedPath, StringComparison.OrdinalIgnoreCase));
        capturedDescriptor.FileAccess.AllowedWritePaths
            .ShouldContain(p => p.Equals(grantedPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SpawnAsync_GrantedPaths_OutsideParentGrant_AreFiltered()
    {
        // Parent has a restricted FileAccess — paths outside parent's grant must be filtered.
        var fileSystem = new MockFileSystem();
        var homePath = "/botnexus-home";
        var workspaceManager = new FileAgentWorkspaceManager(
            new BotNexus.Gateway.Configuration.BotNexusHome(fileSystem, homePath),
            fileSystem);
        var allowedPath = Path.GetFullPath("/allowed/zone");
        var forbiddenPath = Path.GetFullPath("/secret/configs");

        var parentPolicy = new FileAccessPolicy
        {
            AllowedReadPaths = [allowedPath],
            AllowedWritePaths = [allowedPath]
        };

        AgentDescriptor? capturedDescriptor = null;
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(ParentAgentId)).Returns(MakeParentDescriptor(fileAccess: parentPolicy));
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
            .Callback<AgentDescriptor>(d => capturedDescriptor = d);

        var (manager, _) = CreateManager(workspaceManager: workspaceManager, registry: registry);

        await manager.SpawnAsync(CreateSpawnRequest(
            grantedPaths: [allowedPath, forbiddenPath]));

        capturedDescriptor.ShouldNotBeNull();
        var readPaths = capturedDescriptor!.FileAccess!.AllowedReadPaths;
        readPaths.ShouldContain(p => p.Equals(allowedPath, StringComparison.OrdinalIgnoreCase),
            "path within parent grant should be included");
        readPaths.ShouldNotContain(p => p.Equals(forbiddenPath, StringComparison.OrdinalIgnoreCase),
            "path outside parent grant must be filtered");
    }

    [Fact]
    public async Task SpawnAsync_DefaultIsolation_DoesNotIncludeParentWorkspace()
    {
        // Default spawn (no shareWorkspace, no grantedPaths) — parent workspace should NOT appear.
        var fileSystem = new MockFileSystem();
        var homePath = "/botnexus-home";
        var workspaceManager = new FileAgentWorkspaceManager(
            new BotNexus.Gateway.Configuration.BotNexusHome(fileSystem, homePath),
            fileSystem);

        AgentDescriptor? capturedDescriptor = null;
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(ParentAgentId)).Returns(MakeParentDescriptor());
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
            .Callback<AgentDescriptor>(d => capturedDescriptor = d);

        var (manager, _) = CreateManager(workspaceManager: workspaceManager, registry: registry);

        await manager.SpawnAsync(CreateSpawnRequest());

        capturedDescriptor.ShouldNotBeNull();
        var parentWorkspacePath = workspaceManager.GetWorkspacePath(ParentAgentId.Value);
        // FileAccess may be null (no policy) or may only include child temp workspace.
        if (capturedDescriptor!.FileAccess is not null)
        {
            capturedDescriptor.FileAccess.AllowedReadPaths
                .ShouldNotContain(p => p.Equals(parentWorkspacePath, StringComparison.OrdinalIgnoreCase),
                    "parent workspace must NOT appear in child grants by default");
        }
    }

    // ---- Helpers ----------------------------------------------------------------

    private static SubAgentSpawnRequest CreateSpawnRequest(
        bool shareParentWorkspace = false,
        IReadOnlyList<string>? grantedPaths = null)
        => new()
        {
            ParentAgentId = ParentAgentId,
            ParentSessionId = ParentSessionId,
            Task = "Do isolated work",
            ShareParentWorkspace = shareParentWorkspace,
            GrantedPaths = grantedPaths ?? []
        };

    private static AgentDescriptor MakeParentDescriptor(FileAccessPolicy? fileAccess = null)
        => new()
        {
            AgentId = ParentAgentId,
            DisplayName = "Parent Agent",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            FileAccess = fileAccess
        };

    private static (DefaultSubAgentManager Manager, Mock<IAgentRegistry> Registry) CreateManager(
        IAgentWorkspaceManager? workspaceManager = null,
        Mock<IAgentRegistry>? registry = null,
        Action<AgentId>? onChildAgentId = null)
    {
        var isCallerRegistry = registry is not null;
        registry ??= new Mock<IAgentRegistry>();

        if (!isCallerRegistry)
        {
            registry.Setup(r => r.Get(ParentAgentId)).Returns(MakeParentDescriptor());
            registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        }

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(ParentAgentId);
        handle.SetupGet(h => h.SessionId).Returns(ParentSessionId);
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Callback<AgentId, SessionId, CancellationToken>((agentId, _, _) => onChildAgentId?.Invoke(agentId))
            .ReturnsAsync(handle.Object);
        supervisor
            .Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            Mock.Of<IChannelDispatcher>(),
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance,
            workspaceManager: workspaceManager);

        return (manager, registry);
    }
}
