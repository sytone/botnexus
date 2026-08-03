using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Covers issue #2650: <c>grantedPaths</c> conferred read access only, and the resulting write
/// refusal was discovered by the sub-agent at its first <c>write</c>/<c>edit</c>, mid-run. These
/// tests pin (1) the read-only semantics of <c>grantedPaths</c>, (2) the new
/// <see cref="SubAgentSpawnRequest.GrantedWritePaths"/> write grant end-to-end through
/// <see cref="DefaultPathValidator"/>, (3) the spawn-time diagnostic, and (4) that
/// <c>shareWorkspace</c> still grants read+write on the parent workspace.
/// </summary>
public sealed class SubAgentGrantedWritePathsTests : IDisposable
{
    private const string ParentId = "parent-agent";

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "botnexus-tests", Guid.NewGuid().ToString("N"));

    public SubAgentGrantedWritePathsTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the suite.
        }
    }

    // ---------------- AC1: grantedPaths stay read-only ----------------

    [Fact]
    public void GrantedPaths_AppearInReadPathsAndNotWritePaths()
    {
        var manager = BuildManager(out _);
        var granted = Path.Combine(_tempRoot, "readonly-dir");
        Directory.CreateDirectory(granted);

        var policy = manager.BuildChildFileAccessPolicy(Request(grantedPaths: [granted]));

        policy.ShouldNotBeNull();
        policy!.AllowedReadPaths.ShouldContain(p => p.Equals(Path.GetFullPath(granted), StringComparison.OrdinalIgnoreCase));
        policy.AllowedWritePaths.ShouldBeEmpty();
    }

    [Fact]
    public void GrantedPaths_AreRefusedForWriteThroughThePathValidator()
    {
        var manager = BuildManager(out _);
        var granted = Path.Combine(_tempRoot, "readonly-dir");
        Directory.CreateDirectory(granted);
        var target = Path.Combine(granted, "output.txt");

        var policy = manager.BuildChildFileAccessPolicy(Request(grantedPaths: [granted]));
        var validator = new DefaultPathValidator(policy, ChildWorkspace());

        validator.ValidateAndResolve(target, FileAccessMode.Read).ShouldNotBeNull();
        validator.ValidateAndResolve(target, FileAccessMode.Write).ShouldBeNull();
    }

    // ---------------- AC2: grantedWritePaths confer write, end-to-end ----------------

    [Fact]
    public void GrantedWritePaths_AppearInBothReadAndWritePaths()
    {
        var manager = BuildManager(out _);
        var writable = Path.Combine(_tempRoot, "worktree");
        Directory.CreateDirectory(writable);

        var policy = manager.BuildChildFileAccessPolicy(Request(grantedWritePaths: [writable]));

        policy.ShouldNotBeNull();
        var resolved = Path.GetFullPath(writable);
        policy!.AllowedReadPaths.ShouldContain(p => p.Equals(resolved, StringComparison.OrdinalIgnoreCase));
        policy.AllowedWritePaths.ShouldContain(p => p.Equals(resolved, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GrantedWritePaths_AllowWriteThroughThePathValidator()
    {
        var manager = BuildManager(out _);
        var writable = Path.Combine(_tempRoot, "worktree");
        Directory.CreateDirectory(writable);
        var target = Path.Combine(writable, "src", "new-file.cs");

        var policy = manager.BuildChildFileAccessPolicy(Request(grantedWritePaths: [writable]));
        var validator = new DefaultPathValidator(policy, ChildWorkspace());

        validator.ValidateAndResolve(target, FileAccessMode.Write).ShouldNotBeNull();
        validator.ValidateAndResolve(target, FileAccessMode.Read).ShouldNotBeNull();
    }

    [Fact]
    public void GrantedWritePaths_DoNotWidenAccessToSiblingDirectories()
    {
        var manager = BuildManager(out _);
        var writable = Path.Combine(_tempRoot, "worktree");
        var sibling = Path.Combine(_tempRoot, "other");
        Directory.CreateDirectory(writable);
        Directory.CreateDirectory(sibling);

        var policy = manager.BuildChildFileAccessPolicy(Request(grantedWritePaths: [writable]));
        var validator = new DefaultPathValidator(policy, ChildWorkspace());

        validator.ValidateAndResolve(Path.Combine(sibling, "f.txt"), FileAccessMode.Write).ShouldBeNull();
    }

    [Fact]
    public void GrantedWritePathsWithBlankEntries_AreFiltered()
    {
        var manager = BuildManager(out _);
        var writable = Path.Combine(_tempRoot, "worktree");
        Directory.CreateDirectory(writable);

        var policy = manager.BuildChildFileAccessPolicy(
            Request(grantedWritePaths: [writable, "", "   "]));

        policy.ShouldNotBeNull();
        policy!.AllowedWritePaths.Count.ShouldBe(1);
        policy.AllowedReadPaths.Count.ShouldBe(1);
    }

    // ---------------- AC3: spawn-time diagnostic ----------------

    [Fact]
    public void WriteCapableChildWithReadOnlyGrantedPaths_WarnsAtSpawnTime()
    {
        var manager = BuildManager(out var logger);

        manager.WarnOnUnwritableGrantedPaths(
            Request(grantedPaths: ["/data/shared"]),
            toolIds: ["read", "write", "edit"]);

        VerifyWarningCount(logger, 1);
    }

    [Fact]
    public void WriteCapableChildWithGrantedWritePaths_DoesNotWarn()
    {
        var manager = BuildManager(out var logger);

        manager.WarnOnUnwritableGrantedPaths(
            Request(grantedPaths: ["/data/shared"], grantedWritePaths: ["/data/out"]),
            toolIds: ["read", "write", "edit"]);

        VerifyWarningCount(logger, 0);
    }

    [Fact]
    public void WriteCapableChildWithShareWorkspace_DoesNotWarn()
    {
        var manager = BuildManager(out var logger);

        manager.WarnOnUnwritableGrantedPaths(
            Request(shareWorkspace: true, grantedPaths: ["/data/shared"]),
            toolIds: ["read", "write"]);

        VerifyWarningCount(logger, 0);
    }

    [Fact]
    public void ReadOnlyChildWithGrantedPaths_DoesNotWarn()
    {
        var manager = BuildManager(out var logger);

        manager.WarnOnUnwritableGrantedPaths(
            Request(grantedPaths: ["/data/shared"]),
            toolIds: ["read", "grep", "glob"]);

        VerifyWarningCount(logger, 0);
    }

    [Fact]
    public void NoGrantedPaths_DoesNotWarn()
    {
        var manager = BuildManager(out var logger);

        manager.WarnOnUnwritableGrantedPaths(Request(), toolIds: ["read", "write"]);

        VerifyWarningCount(logger, 0);
    }

    // ---------------- AC4: shareWorkspace still grants read+write ----------------

    [Fact]
    public void ShareWorkspace_StillGrantsParentWorkspaceReadAndWrite()
    {
        var parentWorkspace = Path.Combine(_tempRoot, "parent-workspace");
        Directory.CreateDirectory(parentWorkspace);
        var manager = BuildManager(out _, parentWorkspacePath: parentWorkspace);

        var policy = manager.BuildChildFileAccessPolicy(Request(shareWorkspace: true));

        policy.ShouldNotBeNull();
        policy!.AllowedReadPaths.ShouldContain(p => p.Equals(parentWorkspace, StringComparison.OrdinalIgnoreCase));
        policy.AllowedWritePaths.ShouldContain(p => p.Equals(parentWorkspace, StringComparison.OrdinalIgnoreCase));

        var validator = new DefaultPathValidator(policy, ChildWorkspace());
        validator.ValidateAndResolve(Path.Combine(parentWorkspace, "f.txt"), FileAccessMode.Write)
            .ShouldNotBeNull();
    }

    [Fact]
    public void ShareWorkspaceWithGrantedWritePaths_KeepsBothWritablesSeparate()
    {
        var parentWorkspace = Path.Combine(_tempRoot, "parent-workspace");
        var writable = Path.Combine(_tempRoot, "worktree");
        Directory.CreateDirectory(parentWorkspace);
        Directory.CreateDirectory(writable);
        var manager = BuildManager(out _, parentWorkspacePath: parentWorkspace);

        var policy = manager.BuildChildFileAccessPolicy(
            Request(shareWorkspace: true, grantedPaths: ["/data/shared"], grantedWritePaths: [writable]));

        policy.ShouldNotBeNull();
        // parent workspace + granted read + granted write
        policy!.AllowedReadPaths.Count.ShouldBe(3);
        // parent workspace + granted write only
        policy.AllowedWritePaths.Count.ShouldBe(2);
    }

    // ---------------- helpers ----------------

    private string ChildWorkspace()
    {
        var path = Path.Combine(_tempRoot, "child-workspace");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void VerifyWarningCount(Mock<ILogger<DefaultSubAgentManager>> logger, int times)
        => logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(times));

    private static SubAgentSpawnRequest Request(
        bool shareWorkspace = false,
        IReadOnlyList<string>? grantedPaths = null,
        IReadOnlyList<string>? grantedWritePaths = null)
        => new()
        {
            ParentAgentId = AgentId.From(ParentId),
            ParentSessionId = SessionId.From($"{ParentId}-session"),
            Task = "Do work",
            TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("inherited-conv"),
            Mode = new Embody(SubAgentArchetype.General),
            ShareWorkspace = shareWorkspace,
            GrantedPaths = grantedPaths,
            GrantedWritePaths = grantedWritePaths
        };

    private static DefaultSubAgentManager BuildManager(
        out Mock<ILogger<DefaultSubAgentManager>> logger,
        string? parentWorkspacePath = null)
    {
        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(It.IsAny<AgentId>()))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From(ParentId),
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "openai"
            });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        if (parentWorkspacePath is not null)
        {
            workspaceManager
                .Setup(w => w.GetWorkspacePath(ParentId))
                .Returns(parentWorkspacePath);
        }

        logger = new Mock<ILogger<DefaultSubAgentManager>>();

        return new DefaultSubAgentManager(
            new Mock<IAgentSupervisor>().Object,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            logger.Object,
            workspaceManager: workspaceManager.Object);
    }
}
