using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentWorkspaceIsolationTests
{
    private static readonly AgentId ParentAgentId = AgentId.From("parent-agent");
    private static readonly SessionId ParentSessionId = SessionId.From("parent-session");
    private static readonly ConversationId ConvId = ConversationId.From("conv-1");

    [Fact]
    public async Task SpawnAsync_DefaultIsolation_ChildHasNoFileAccess()
    {
        // Default: ShareWorkspace=false, GrantedPaths=null
        AgentDescriptor? registeredDescriptor = null;
        var (manager, _) = CreateManager(onRegister: d => registeredDescriptor = d);

        await manager.SpawnAsync(CreateRequest());

        registeredDescriptor.ShouldNotBeNull();
        // Default isolation: child inherits parent descriptor's FileAccess (null in test)
        registeredDescriptor!.FileAccess.ShouldBeNull();
    }

    [Fact]
    public async Task SpawnAsync_ShareWorkspace_ChildHasParentWorkspaceInAllowedPaths()
    {
        AgentDescriptor? registeredDescriptor = null;
        var (manager, _) = CreateManager(
            onRegister: d => registeredDescriptor = d,
            parentWorkspacePath: "/home/user/.botnexus/agents/parent-agent/workspace");

        await manager.SpawnAsync(CreateRequest(shareWorkspace: true));

        registeredDescriptor.ShouldNotBeNull();
        registeredDescriptor!.FileAccess.ShouldNotBeNull();
        registeredDescriptor.FileAccess!.AllowedReadPaths.ShouldContain(
            p => p.Contains("parent-agent"));
        registeredDescriptor.FileAccess!.AllowedWritePaths.ShouldContain(
            p => p.Contains("parent-agent"));
    }

    [Fact]
    public async Task SpawnAsync_GrantedPaths_ChildHasReadOnlyAccess()
    {
        AgentDescriptor? registeredDescriptor = null;
        var (manager, _) = CreateManager(onRegister: d => registeredDescriptor = d);

        await manager.SpawnAsync(CreateRequest(
            grantedPaths: ["/data/shared", "/repos/project"]));

        registeredDescriptor.ShouldNotBeNull();
        registeredDescriptor!.FileAccess.ShouldNotBeNull();
        registeredDescriptor.FileAccess!.AllowedReadPaths.Count.ShouldBe(2);
        // Granted paths are read-only, not write
        registeredDescriptor.FileAccess!.AllowedWritePaths.Count.ShouldBe(0);
    }

    [Fact]
    public async Task SpawnAsync_ShareWorkspaceAndGrantedPaths_CombinesBoth()
    {
        AgentDescriptor? registeredDescriptor = null;
        var (manager, _) = CreateManager(
            onRegister: d => registeredDescriptor = d,
            parentWorkspacePath: "/home/user/.botnexus/agents/parent-agent/workspace");

        await manager.SpawnAsync(CreateRequest(
            shareWorkspace: true,
            grantedPaths: ["/data/shared"]));

        registeredDescriptor.ShouldNotBeNull();
        registeredDescriptor!.FileAccess.ShouldNotBeNull();
        // Parent workspace (read+write) + granted path (read only)
        registeredDescriptor.FileAccess!.AllowedReadPaths.Count.ShouldBe(2);
        registeredDescriptor.FileAccess!.AllowedWritePaths.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SpawnAsync_ShareWorkspaceFalse_NoParentWorkspaceAccess()
    {
        AgentDescriptor? registeredDescriptor = null;
        var (manager, _) = CreateManager(
            onRegister: d => registeredDescriptor = d,
            parentWorkspacePath: "/home/user/.botnexus/agents/parent-agent/workspace");

        await manager.SpawnAsync(CreateRequest(shareWorkspace: false));

        registeredDescriptor.ShouldNotBeNull();
        // No file access policy when ShareWorkspace is false and no granted paths
        registeredDescriptor!.FileAccess.ShouldBeNull();
    }

    [Fact]
    public async Task SpawnAsync_GrantedPathsWithBlankEntries_FiltersBlankPaths()
    {
        AgentDescriptor? registeredDescriptor = null;
        var (manager, _) = CreateManager(onRegister: d => registeredDescriptor = d);

        await manager.SpawnAsync(CreateRequest(
            grantedPaths: ["/valid/path", "", "  ", "/another/path"]));

        registeredDescriptor.ShouldNotBeNull();
        registeredDescriptor!.FileAccess.ShouldNotBeNull();
        registeredDescriptor.FileAccess!.AllowedReadPaths.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SpawnRequest_DefaultShareWorkspace_IsFalse()
    {
        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = ParentAgentId,
            ParentSessionId = ParentSessionId,
            Task = "test",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConvId
        };

        request.ShareWorkspace.ShouldBeFalse();
        request.GrantedPaths.ShouldBeNull();
    }

    private static SubAgentSpawnRequest CreateRequest(
        bool shareWorkspace = false,
        IReadOnlyList<string>? grantedPaths = null)
        => new()
        {
            ParentAgentId = ParentAgentId,
            ParentSessionId = ParentSessionId,
            Task = "Investigate timeout",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConvId,
            ShareWorkspace = shareWorkspace,
            GrantedPaths = grantedPaths
        };

    private static (DefaultSubAgentManager Manager, Mock<IAgentSupervisor> Supervisor) CreateManager(
        Action<AgentDescriptor>? onRegister = null,
        string? parentWorkspacePath = null)
    {
        var parentDescriptor = new AgentDescriptor
        {
            AgentId = ParentAgentId,
            DisplayName = "Parent Agent",
            ModelId = "gpt-4",
            ApiProvider = "openai"
        };

        var supervisor = new Mock<IAgentSupervisor>();
        var childHandle = new Mock<IAgentHandle>();
        childHandle.SetupGet(h => h.AgentId).Returns(ParentAgentId);
        childHandle.SetupGet(h => h.SessionId).Returns(ParentSessionId);
        childHandle.SetupGet(h => h.IsRunning).Returns(false);
        childHandle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });
        childHandle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(ParentAgentId)).Returns(parentDescriptor);
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        if (onRegister is not null)
        {
            registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
                .Callback<AgentDescriptor>(d => onRegister(d));
        }

        var activity = new Mock<IActivityBroadcaster>();
        var dispatcher = new Mock<IChannelDispatcher>();
        var options = new Mock<IOptionsMonitor<GatewayOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new GatewayOptions());
        var logger = NullLogger<DefaultSubAgentManager>.Instance;

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        if (parentWorkspacePath is not null)
        {
            workspaceManager.Setup(w => w.GetWorkspacePath(ParentAgentId.Value))
                .Returns(parentWorkspacePath);
        }

        var manager = new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            activity.Object,
            dispatcher.Object,
            options.Object,
            logger,
            workspaceManager: workspaceManager.Object);

        return (manager, supervisor);
    }
}
