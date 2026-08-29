using BotNexus.Agent.Core.Tools;

namespace BotNexus.Agent.Core.Tests.Tools;

/// <summary>
/// Acceptance coverage for issue #3569 AC5: a tool that finds its working directory missing must
/// return an explicit workspace-reclaimed diagnostic naming the sub-agent, instead of the raw
/// OS-level <c>The directory name is invalid</c> / <c>Base directory ... does not exist</c>.
/// <para>
/// The original failures produced 66 tool errors across 37 sub-agents, and every one of them read
/// as a generic path fault. The sub-agent had no signal that its workspace had been reclaimed by
/// the platform, so it kept retrying and burned its whole remaining budget before returning a
/// confident-sounding but wrong summary to its parent. The message is the fix.
/// </para>
/// </summary>
public sealed class ReclaimedWorkspacePreflightTests
{
    private const string SubAgentWorkspace =
        @"C:\Temp\botnexus-subagent-workspaces\tinker--subagent--warden--7be71aa4bf9146d3\workspace";

    /// <summary>Happy path: an existing working directory produces no diagnostic at all.</summary>
    [Fact]
    public void Describe_ReturnsNull_WhenWorkingDirectoryExists()
    {
        var message = ReclaimedWorkspacePreflight.Describe(SubAgentWorkspace, _ => true);

        message.ShouldBeNull();
    }

    /// <summary>
    /// The core AC5 assertion. A missing sub-agent workspace yields a diagnostic that names the
    /// sub-agent id, says the workspace was reclaimed, and tells the run to stop rather than retry.
    /// </summary>
    [Fact]
    public void Describe_NamesSubAgentAndReclamation_WhenSubAgentWorkspaceIsMissing()
    {
        var message = ReclaimedWorkspacePreflight.Describe(SubAgentWorkspace, _ => false);

        message.ShouldNotBeNull();
        message.ShouldContain("tinker--subagent--warden--7be71aa4bf9146d3");
        message.ShouldContain("reclaimed");
        message.ShouldContain("stop");
    }

    /// <summary>
    /// The diagnostic must NOT read like a caller mistake. The sub-agent did nothing wrong: the
    /// platform deleted its workspace underneath it, and the message has to say so, otherwise the
    /// agent concludes it passed a bad path and retries with a different one.
    /// </summary>
    [Fact]
    public void Describe_AttributesTheFailureToThePlatform_NotToTheCaller()
    {
        var message = ReclaimedWorkspacePreflight.Describe(SubAgentWorkspace, _ => false);

        message.ShouldNotBeNull();
        message.ShouldNotContain("The directory name is invalid");
        message.ShouldContain("#3569");
    }

    /// <summary>
    /// Sad path / scope guard. A NON-sub-agent workspace (a top-level registered agent) is not
    /// subject to the sweep, so a missing directory there is a genuine configuration fault and must
    /// keep its ordinary error. Claiming "reclaimed mid-run" there would be a false diagnosis.
    /// </summary>
    [Fact]
    public void Describe_ReturnsNull_ForMissingNonSubAgentDirectory()
    {
        var message = ReclaimedWorkspacePreflight.Describe(
            Path.Combine(Path.GetTempPath(), ".botnexus", "agents", "farnsworth", "workspace"),
            _ => false);

        message.ShouldBeNull();
    }

    /// <summary>A null or blank working directory is not a reclaimed workspace.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Describe_ReturnsNull_ForMissingWorkingDirectoryValue(string? workingDirectory)
    {
        var message = ReclaimedWorkspacePreflight.Describe(workingDirectory, _ => false);

        message.ShouldBeNull();
    }

    /// <summary>
    /// The probe throwing must not convert a tool failure into a preflight crash. No diagnosis is
    /// better than an exception thrown from the diagnostic path itself.
    /// </summary>
    [Fact]
    public void Describe_ReturnsNull_WhenExistenceProbeThrows()
    {
        var message = ReclaimedWorkspacePreflight.Describe(
            SubAgentWorkspace,
            _ => throw new UnauthorizedAccessException("denied"));

        message.ShouldBeNull();
    }

    /// <summary>
    /// The marker is matched anywhere in the path, so both the directory-with-workspace-suffix form
    /// used by the tools and the bare agent directory form resolve the same sub-agent id.
    /// </summary>
    [Fact]
    public void Describe_ResolvesSubAgentId_WithoutWorkspaceSuffix()
    {
        var message = ReclaimedWorkspacePreflight.Describe(
            @"C:\Temp\botnexus-subagent-workspaces\nova--subagent--coder--ca861848",
            _ => false);

        message.ShouldNotBeNull();
        message.ShouldContain("nova--subagent--coder--ca861848");
    }

    /// <summary>
    /// <see cref="ReclaimedWorkspacePreflight.ThrowIfReclaimed"/> is the enforcement seam the tools
    /// call: it raises the diagnostic as an exception so the failure reaches the agent as the tool
    /// error text.
    /// </summary>
    [Fact]
    public void ThrowIfReclaimed_Throws_WithTheDiagnostic_WhenWorkspaceIsMissing()
    {
        var exception = Should.Throw<DirectoryNotFoundException>(
            () => ReclaimedWorkspacePreflight.ThrowIfReclaimed(SubAgentWorkspace, _ => false));

        exception.Message.ShouldContain("tinker--subagent--warden--7be71aa4bf9146d3");
        exception.Message.ShouldContain("reclaimed");
    }

    /// <summary>The enforcement seam is silent whenever the workspace is present.</summary>
    [Fact]
    public void ThrowIfReclaimed_DoesNotThrow_WhenWorkspaceExists()
    {
        Should.NotThrow(() => ReclaimedWorkspacePreflight.ThrowIfReclaimed(SubAgentWorkspace, _ => true));
    }
}
