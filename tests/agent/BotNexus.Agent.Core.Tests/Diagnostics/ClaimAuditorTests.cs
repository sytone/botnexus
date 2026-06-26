using BotNexus.Agent.Core.Diagnostics;

namespace BotNexus.Agent.Core.Tests.Diagnostics;

/// <summary>
/// Tests for the post-turn claim auditor (#1600, control #1 of #1551). The auditor
/// scans a final assistant message for artifact-shaped claims (issue filed, PR
/// opened, file written, sent, deployed) and flags any claim that has no backing
/// tool call among the tools actually invoked during the run. The reproducing
/// failure (#1551 evidence): the agent narrated "filed issue #N" and "ran a security
/// audit" with <strong>no</strong> tool calls emitted that turn.
/// </summary>
public class ClaimAuditorTests
{
    private static readonly ClaimAuditOptions Defaults = ClaimAuditOptions.CreateDefault();

    // ---- The reproducing case: artifact claim with ZERO tools invoked ----

    [Fact]
    public void Audit_IssueFiledClaim_NoToolsInvoked_IsFlagged()
    {
        // The exact #1551 failure: "I filed issue #1234" with no tool calls that turn.
        var result = ClaimAuditor.Audit(
            "Good news, everyone! I filed issue #1234 to track the regression.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Defaults);

        result.HasUnbackedClaims.ShouldBeTrue();
        result.UnbackedClaims.ShouldContain(c => c.Category == ClaimCategory.IssueFiled);
    }

    [Fact]
    public void Audit_IssueFiledClaim_WithBackingShellTool_IsNotFlagged()
    {
        // GitHub operations go through the shell/exec tool in BotNexus, so a genuine
        // `gh issue create` shows up as a shell invocation. No false positive.
        var result = ClaimAuditor.Audit(
            "I filed issue #1234 to track the regression.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shell" },
            Defaults);

        result.HasUnbackedClaims.ShouldBeFalse();
        result.UnbackedClaims.ShouldBeEmpty();
    }

    [Fact]
    public void Audit_SecurityAuditNarration_NoToolsInvoked_IsFlagged()
    {
        // The second half of the #1551 failure: claiming a multi-check audit "passed"
        // with no tool calls. A check-table style "verified" assertion with zero tools
        // is unbacked.
        var result = ClaimAuditor.Audit(
            "I ran the security audit and verified all checks passed: CodeQL pass, secrets pass.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Defaults);

        result.HasUnbackedClaims.ShouldBeTrue();
    }

    // ---- PR / URL / file / sent / deployed claim categories ----

    [Fact]
    public void Audit_PrOpenedClaim_NoTools_IsFlagged()
    {
        var result = ClaimAuditor.Audit(
            "I opened PR #42 against main.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Defaults);

        result.UnbackedClaims.ShouldContain(c => c.Category == ClaimCategory.PullRequest);
    }

    [Fact]
    public void Audit_ArtifactUrlClaim_NoTools_IsFlagged()
    {
        var result = ClaimAuditor.Audit(
            "Done: https://github.com/Sytone/botnexus/pull/99 is ready for review.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Defaults);

        result.UnbackedClaims.ShouldContain(c => c.Category == ClaimCategory.ArtifactUrl);
    }

    [Fact]
    public void Audit_WrotePathClaim_WithWriteTool_IsNotFlagged()
    {
        var result = ClaimAuditor.Audit(
            "I wrote `src/Foo.cs` with the new implementation.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "write" },
            Defaults);

        result.HasUnbackedClaims.ShouldBeFalse();
    }

    [Fact]
    public void Audit_WrotePathClaim_NoTools_IsFlagged()
    {
        var result = ClaimAuditor.Audit(
            "I wrote `src/Foo.cs` with the new implementation.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Defaults);

        result.UnbackedClaims.ShouldContain(c => c.Category == ClaimCategory.FileWritten);
    }

    [Fact]
    public void Audit_SentClaim_WithMessagingTool_IsNotFlagged()
    {
        var result = ClaimAuditor.Audit(
            "I sent the summary to the team channel.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "teams" },
            Defaults);

        result.HasUnbackedClaims.ShouldBeFalse();
    }

    [Fact]
    public void Audit_DeployedClaim_NoTools_IsFlagged()
    {
        var result = ClaimAuditor.Audit(
            "I deployed the new build to production.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Defaults);

        result.UnbackedClaims.ShouldContain(c => c.Category == ClaimCategory.Deployed);
    }

    // ---- No false positives on benign / non-claim text ----

    [Fact]
    public void Audit_NoArtifactClaims_ReturnsEmpty()
    {
        var result = ClaimAuditor.Audit(
            "I reviewed the code and it looks correct. The compaction trigger is sound.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Defaults);

        result.HasUnbackedClaims.ShouldBeFalse();
        result.UnbackedClaims.ShouldBeEmpty();
    }

    [Fact]
    public void Audit_FutureTenseIntent_IsNotFlagged()
    {
        // Future-tense / planning language is not an artifact claim.
        var result = ClaimAuditor.Audit(
            "I will file issue #1234 and open a PR once I have confirmed the fix.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Defaults);

        result.HasUnbackedClaims.ShouldBeFalse();
    }

    [Fact]
    public void Audit_ReferencingExistingIssueNumber_IsNotFlagged()
    {
        // Merely mentioning an issue number (not claiming to have filed it) is not a claim.
        var result = ClaimAuditor.Audit(
            "This change relates to #1234 and the parent epic #1551.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Defaults);

        result.HasUnbackedClaims.ShouldBeFalse();
    }

    // ---- Options behaviour ----

    [Fact]
    public void Audit_Disabled_ReturnsEmptyWithoutScanning()
    {
        var disabled = Defaults with { Enabled = false };
        var result = ClaimAuditor.Audit(
            "I filed issue #1234.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            disabled);

        result.HasUnbackedClaims.ShouldBeFalse();
        result.UnbackedClaims.ShouldBeEmpty();
    }

    [Fact]
    public void Audit_BlockMode_IsReflectedInResult()
    {
        var block = Defaults with { Mode = ClaimAuditMode.Block };
        var result = ClaimAuditor.Audit(
            "I filed issue #1234.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            block);

        result.HasUnbackedClaims.ShouldBeTrue();
        result.Mode.ShouldBe(ClaimAuditMode.Block);
        result.ShouldBlock.ShouldBeTrue();
    }

    [Fact]
    public void Audit_WarnMode_DoesNotBlock()
    {
        var result = ClaimAuditor.Audit(
            "I filed issue #1234.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Defaults);

        result.HasUnbackedClaims.ShouldBeTrue();
        result.Mode.ShouldBe(ClaimAuditMode.Warn);
        result.ShouldBlock.ShouldBeFalse();
    }

    [Fact]
    public void Audit_EmptyMessage_ReturnsEmpty()
    {
        ClaimAuditor.Audit(string.Empty, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Defaults)
            .HasUnbackedClaims.ShouldBeFalse();
        ClaimAuditor.Audit(null!, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Defaults)
            .HasUnbackedClaims.ShouldBeFalse();
    }

    [Fact]
    public void Audit_DetectedClaimCarriesEvidenceSnippet()
    {
        var result = ClaimAuditor.Audit(
            "Good news! I filed issue #1234 to track this.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Defaults);

        var claim = result.UnbackedClaims.ShouldHaveSingleItem();
        claim.Snippet.ShouldNotBeNullOrWhiteSpace();
        claim.Snippet.ShouldContain("1234");
    }
}
