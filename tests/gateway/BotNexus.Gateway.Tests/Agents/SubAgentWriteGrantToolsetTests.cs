using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Covers issue #3562: <c>grantedWritePaths</c> (and <c>shareWorkspace</c>) could grant a sub-agent
/// a writable location while the archetype independently resolved a toolset with no
/// <c>write</c>/<c>edit</c>/<c>exec</c>. The worker discovered the contradiction only when it tried
/// to produce its deliverable, at the end of its run, and its output was lost.
/// </summary>
/// <remarks>
/// These tests pin the REJECTION (not a warning) and its message content, the
/// <c>shareWorkspace</c> arm, the explicit <c>tools</c> override escape hatch, and that the
/// pre-existing #2650 <c>WarnOnUnwritableGrantedPaths</c> path is untouched by the new check.
/// </remarks>
public sealed class SubAgentWriteGrantToolsetTests
{
    private const string ParentId = "parent-agent";

    // ---------------- AC1/AC2: read-only archetype + grantedWritePaths is rejected ----------------

    [Fact]
    public void ReadOnlyArchetypeWithGrantedWritePaths_IsRejected()
    {
        var manager = BuildManager(out _);
        var researcher = SubAgentArchetype.FromString("researcher");
        var toolIds = BuiltInArchetypes.GetProfile(researcher)!.ToolIds;

        var ex = Should.Throw<InvalidOperationException>(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(grantedWritePaths: ["/data/out"]),
                toolIds,
                researcher));

        // AC2: the message names the archetype and the write-capable tools that are missing.
        ex.Message.ShouldContain("researcher");
        ex.Message.ShouldContain("grantedWritePaths");
        ex.Message.ShouldContain("write");
        ex.Message.ShouldContain("edit");
        ex.Message.ShouldContain("exec");
    }

    [Fact]
    public void ReviewerArchetypeWithGrantedWritePaths_IsRejected()
    {
        var manager = BuildManager(out _);
        var reviewer = SubAgentArchetype.FromString("reviewer");

        Should.Throw<InvalidOperationException>(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(grantedWritePaths: ["/data/out"]),
                BuiltInArchetypes.GetProfile(reviewer)!.ToolIds,
                reviewer));
    }

    [Fact]
    public void RejectionIsThrownNotLogged()
    {
        var manager = BuildManager(out var logger);
        var researcher = SubAgentArchetype.FromString("researcher");

        Should.Throw<InvalidOperationException>(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(grantedWritePaths: ["/data/out"]),
                BuiltInArchetypes.GetProfile(researcher)!.ToolIds,
                researcher));

        // The remedy is rejection, not a parent-side warning the worker never sees.
        VerifyWarningCount(logger, 0);
    }

    // ---------------- AC3: shareWorkspace is the same contradiction ----------------

    [Fact]
    public void ReadOnlyArchetypeWithShareWorkspace_IsRejected()
    {
        var manager = BuildManager(out _);
        var researcher = SubAgentArchetype.FromString("researcher");

        var ex = Should.Throw<InvalidOperationException>(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(shareWorkspace: true),
                BuiltInArchetypes.GetProfile(researcher)!.ToolIds,
                researcher));

        ex.Message.ShouldContain("shareWorkspace");
        ex.Message.ShouldContain("researcher");
    }

    [Fact]
    public void ReadOnlyArchetypeWithBothGrantShapes_NamesBothInTheMessage()
    {
        var manager = BuildManager(out _);
        var researcher = SubAgentArchetype.FromString("researcher");

        var ex = Should.Throw<InvalidOperationException>(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(shareWorkspace: true, grantedWritePaths: ["/data/out"]),
                BuiltInArchetypes.GetProfile(researcher)!.ToolIds,
                researcher));

        ex.Message.ShouldContain("shareWorkspace and grantedWritePaths");
    }

    // ---------------- AC5: write-capable archetypes and overrides are unaffected ----------------

    [Theory]
    [InlineData("coder")]
    [InlineData("writer")]
    [InlineData("planner")]
    [InlineData("analyst")]
    public void WriteCapableArchetypeWithGrantedWritePaths_IsAllowed(string archetypeName)
    {
        var manager = BuildManager(out var logger);
        var archetype = SubAgentArchetype.FromString(archetypeName);

        Should.NotThrow(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(grantedWritePaths: ["/data/out"]),
                BuiltInArchetypes.GetProfile(archetype)!.ToolIds,
                archetype));

        VerifyWarningCount(logger, 0);
    }

    [Fact]
    public void ExplicitToolsOverrideSupplyingWrite_AgainstReadOnlyArchetype_IsAllowed()
    {
        var manager = BuildManager(out var logger);
        var researcher = SubAgentArchetype.FromString("researcher");

        // The caller opted in explicitly; ResolveSpawnPlan prefers Customizations.ToolIds over the
        // archetype allowlist, so the resolved toolset can write and the grant is coherent.
        Should.NotThrow(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(grantedWritePaths: ["/data/out"]),
                toolIds: ["read", "glob", "write"],
                researcher));

        VerifyWarningCount(logger, 0);
    }

    [Fact]
    public void ExecOnlyToolset_CountsAsWriteCapable()
    {
        var manager = BuildManager(out _);

        // A shell-only worker can produce files by running a command; rejecting it would be a
        // false positive.
        Should.NotThrow(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(grantedWritePaths: ["/data/out"]),
                toolIds: ["read", "shell", "exec"],
                SubAgentArchetype.FromString("analyst")));
    }

    [Fact]
    public void NoResolvedToolRestriction_IsAllowed()
    {
        var manager = BuildManager(out _);

        // null/empty means "inherit the parent's tools" - no restriction was resolved, so nothing
        // contradicts the grant.
        Should.NotThrow(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(grantedWritePaths: ["/data/out"]), toolIds: null, SubAgentArchetype.General));
        Should.NotThrow(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(shareWorkspace: true), toolIds: [], SubAgentArchetype.General));
    }

    [Fact]
    public void NoWriteGrant_IsAllowedEvenForAReadOnlyToolset()
    {
        var manager = BuildManager(out _);
        var researcher = SubAgentArchetype.FromString("researcher");

        Should.NotThrow(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(grantedPaths: ["/data/shared"]),
                BuiltInArchetypes.GetProfile(researcher)!.ToolIds,
                researcher));
    }

    // ---------------- AC4: the #2650 warning path is untouched ----------------

    [Fact]
    public void ReadOnlyGrantedPathsWithWriteCapableTools_StillOnlyWarns()
    {
        var manager = BuildManager(out var logger);

        // The opposite combination: write-capable tools, read-only grantedPaths, no write grant.
        // It must remain a warning and must NOT be pulled into the new rejection.
        manager.WarnOnUnwritableGrantedPaths(
            Request(grantedPaths: ["/data/shared"]),
            toolIds: ["read", "write", "edit"]);
        VerifyWarningCount(logger, 1);

        Should.NotThrow(() =>
            manager.ValidateWriteGrantIsUsable(
                Request(grantedPaths: ["/data/shared"]),
                toolIds: ["read", "write", "edit"],
                SubAgentArchetype.General));
    }

    // ---------------- archetype catalog invariants ----------------

    [Fact]
    public void WriteCapableToolIds_AreExactlyWriteEditExec()
    {
        BuiltInArchetypes.WriteCapableToolIds
            .Order(StringComparer.Ordinal)
            .ShouldBe(["edit", "exec", "write"]);
    }

    [Fact]
    public void WriteCapableToolIds_MatchIgnoringCase()
        => BuiltInArchetypes.WriteCapableToolIds.Contains("WRITE").ShouldBeTrue();

    // ---------------- helpers ----------------

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

    private static DefaultSubAgentManager BuildManager(out Mock<ILogger<DefaultSubAgentManager>> logger)
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

        logger = new Mock<ILogger<DefaultSubAgentManager>>();

        return new DefaultSubAgentManager(
            new Mock<IAgentSupervisor>().Object,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            logger.Object,
            workspaceManager: new Mock<IAgentWorkspaceManager>().Object);
    }
}
