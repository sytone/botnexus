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
/// Pins that a granted sub-agent inherits the base descriptor's
/// <see cref="FileAccessPolicy.DeniedPaths"/>. The composed policy REPLACES the base one on the
/// child descriptor, so before the fix any spawn requesting a grant dropped the operator's denies
/// entirely - an agent denied <c>~/.ssh</c> kept that protection itself while every sub-agent it
/// spawned with a grant lost it. <see cref="DefaultPathValidator"/> checks denies first and they
/// are the only policy field that subtracts access, so the drop was a silent authorization
/// widening rather than a cosmetic field omission.
/// <para>
/// These cover the composition contract and supply the base policy themselves.
/// <see cref="SubAgentSpawnInheritedDeniedPathsTests"/> covers the call site that has to supply it.
/// </para>
/// </summary>
public sealed class SubAgentInheritedDeniedPathsTests : IDisposable
{
    private const string ParentId = "parent-agent";

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "botnexus-tests", Guid.NewGuid().ToString("N"));

    public SubAgentInheritedDeniedPathsTests() => Directory.CreateDirectory(_tempRoot);

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

    // ---------------- denies survive every grant shape ----------------

    // Denies keep POSIX literals on purpose: composition passes them through verbatim, so a literal
    // that Path.GetFullPath would rewrite (to a drive-qualified path on Windows) is the witness that
    // nobody has started normalizing them. Grants, which ARE resolved, must come off _tempRoot -
    // a rooted-but-not-qualified literal there resolves against whatever drive the tests run on.

    [Fact]
    public void GrantedPaths_InheritDeniedPathsFromBasePolicy()
    {
        var manager = BuildManager();
        var basePolicy = new FileAccessPolicy { DeniedPaths = ["/home/user/.ssh"] };

        var policy = manager.BuildChildFileAccessPolicy(
            Request(grantedPaths: [Path.Combine(_tempRoot, "shared")]), basePolicy);

        policy.ShouldNotBeNull();
        policy!.DeniedPaths.ShouldBe(["/home/user/.ssh"]);
    }

    [Fact]
    public void ShareWorkspace_InheritsDeniedPathsFromBasePolicy()
    {
        var parentWorkspace = Path.Combine(_tempRoot, "parent-workspace");
        Directory.CreateDirectory(parentWorkspace);
        var manager = BuildManager(parentWorkspacePath: parentWorkspace);
        var basePolicy = new FileAccessPolicy { DeniedPaths = ["/home/user/.ssh"] };

        var policy = manager.BuildChildFileAccessPolicy(Request(shareWorkspace: true), basePolicy);

        policy.ShouldNotBeNull();
        policy!.DeniedPaths.ShouldBe(["/home/user/.ssh"]);
    }

    [Fact]
    public void GrantedWritePaths_InheritDeniedPathsFromBasePolicy()
    {
        var manager = BuildManager();
        var basePolicy = new FileAccessPolicy { DeniedPaths = ["/home/user/.ssh", "/etc/**"] };

        var policy = manager.BuildChildFileAccessPolicy(
            Request(grantedWritePaths: [Path.Combine(_tempRoot, "out")]), basePolicy);

        policy.ShouldNotBeNull();
        policy!.DeniedPaths.ShouldBe(["/home/user/.ssh", "/etc/**"]);
    }

    [Fact]
    public void InheritedDenies_DoNotDisturbTheComposedAllowLists()
    {
        // The base policy's allow-lists are deliberately NOT merged: starting the child from only
        // what it was granted narrows it, which is the intended isolation posture.
        var manager = BuildManager();
        var granted = Path.Combine(_tempRoot, "shared");
        var basePolicy = new FileAccessPolicy
        {
            AllowedReadPaths = [Path.Combine(_tempRoot, "base-read")],
            AllowedWritePaths = [Path.Combine(_tempRoot, "base-write")],
            DeniedPaths = ["/home/user/.ssh"]
        };

        var policy = manager.BuildChildFileAccessPolicy(Request(grantedPaths: [granted]), basePolicy);

        policy.ShouldNotBeNull();
        policy!.AllowedReadPaths.ShouldBe([Path.GetFullPath(granted)]);
        policy.AllowedWritePaths.ShouldBeEmpty();
        policy.DeniedPaths.ShouldBe(["/home/user/.ssh"]);
    }

    // ---------------- absent / empty base denies ----------------

    [Fact]
    public void NullBasePolicy_LeavesDeniedPathsEmpty()
    {
        var manager = BuildManager();

        var policy = manager.BuildChildFileAccessPolicy(
            Request(grantedPaths: [Path.Combine(_tempRoot, "shared")]), basePolicy: null);

        policy.ShouldNotBeNull();
        policy!.DeniedPaths.ShouldBeEmpty();
    }

    // ---------------- the inherited deny actually refuses access ----------------

    [Fact]
    public void InheritedDeny_RefusesReadAndWriteInsideAGrantedDirectory()
    {
        // The deny sits *inside* the granted directory, so only the deny can refuse it - proving
        // the carried-forward list reaches the validator rather than merely populating a field.
        var granted = Path.Combine(_tempRoot, "worktree");
        var deniedSubdirectory = Path.Combine(granted, "secrets");
        Directory.CreateDirectory(deniedSubdirectory);

        var manager = BuildManager();
        var basePolicy = new FileAccessPolicy { DeniedPaths = [deniedSubdirectory] };

        var policy = manager.BuildChildFileAccessPolicy(
            Request(grantedWritePaths: [granted]), basePolicy);
        var validator = new DefaultPathValidator(policy, ChildWorkspace());

        var secret = Path.Combine(deniedSubdirectory, "id_rsa");
        validator.CanRead(secret).ShouldBeFalse();
        validator.CanWrite(secret).ShouldBeFalse();

        // Control: the grant itself is intact, so the refusal above is the deny and not a
        // collapsed write grant.
        var sibling = Path.Combine(granted, "output.txt");
        validator.CanRead(sibling).ShouldBeTrue();
        validator.CanWrite(sibling).ShouldBeTrue();
    }

    // ---------------- relative denies are re-anchored before they cross ----------------

    [Fact]
    public void RelativeDeny_IsAnchoredToTheOwnerWorkspace_NotTheChildWorkspace()
    {
        // "secrets" means "<parent workspace>/secrets" to the validator that read it. Carried over
        // verbatim it re-points at the CHILD's workspace, which the grant then reaches straight past.
        var parentWorkspace = Path.Combine(_tempRoot, "parent-workspace");
        var parentSecrets = Path.Combine(parentWorkspace, "secrets");
        Directory.CreateDirectory(parentSecrets);

        var manager = BuildManager(parentWorkspacePath: parentWorkspace);
        var rebased = manager.RebaseInheritedDenies(
            new FileAccessPolicy { DeniedPaths = ["secrets"] }, AgentId.From(ParentId));

        var policy = manager.BuildChildFileAccessPolicy(
            Request(grantedWritePaths: [parentWorkspace]), rebased);
        var validator = new DefaultPathValidator(policy, ChildWorkspace());

        var secret = Path.Combine(parentSecrets, "id_rsa");
        validator.CanRead(secret).ShouldBeFalse();
        validator.CanWrite(secret).ShouldBeFalse();

        // Control: the grant is intact, so the refusal is the re-anchored deny.
        validator.CanRead(Path.Combine(parentWorkspace, "notes.txt")).ShouldBeTrue();
    }

    [Fact]
    public void RootedAndGlobDenies_AreLeftAloneByTheRebase()
    {
        // Neither shape re-binds to a workspace, so rewriting them would change what the operator
        // wrote for no gain - and a glob rewritten onto a workspace stops matching altogether.
        var manager = BuildManager(parentWorkspacePath: Path.Combine(_tempRoot, "parent-workspace"));

        var rebased = manager.RebaseInheritedDenies(
            new FileAccessPolicy { DeniedPaths = ["/home/user/.ssh", "**/*.pem"] },
            AgentId.From(ParentId));

        rebased.ShouldNotBeNull();
        rebased!.DeniedPaths.ShouldBe(["/home/user/.ssh", "**/*.pem"]);
    }

    [Fact]
    public void RelativeOwnerWorkspace_AnchorsWhereTheOwnersValidatorWouldHave()
    {
        // DefaultPathValidator's constructor runs its workspace through Path.GetFullPath, so a
        // relative one still resolves to a real directory. Declining to rebase against it would
        // leave "secrets" re-pointing at the CHILD's workspace - the exact bug the rebase prevents.
        var relativeWorkspace = Path.Combine("..", "relative-parent-workspace");
        var manager = BuildManager(parentWorkspacePath: relativeWorkspace);

        var rebased = manager.RebaseInheritedDenies(
            new FileAccessPolicy { DeniedPaths = ["secrets"] }, AgentId.From(ParentId));

        rebased.ShouldNotBeNull();
        rebased!.DeniedPaths.ShouldBe(
            [Path.GetFullPath(Path.Combine(relativeWorkspace, "secrets"))]);
    }

    // ---------------- helpers ----------------

    private string ChildWorkspace()
    {
        var path = Path.Combine(_tempRoot, "child-workspace");
        Directory.CreateDirectory(path);
        return path;
    }

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

    private static DefaultSubAgentManager BuildManager(string? parentWorkspacePath = null)
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

        return new DefaultSubAgentManager(
            new Mock<IAgentSupervisor>().Object,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            new Mock<ILogger<DefaultSubAgentManager>>().Object,
            workspaceManager: workspaceManager.Object);
    }
}
