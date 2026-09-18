using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Keeps the release workflow wired to the canonical machine-readable release history contract.
/// </summary>
public sealed class ReleaseHistoryManifestArchitectureTests : ArchitectureTest
{
    private string WorkflowPath => Repository.Path(".github", "workflows", "release-cli.yml");
    private string ProducerPath => Repository.Path("scripts", "repo", "New-ReleaseHistoryManifest.ps1");
    private string ContractPath => Repository.Path("docs", "development", "release-history-manifest.md");

    [Fact]
    public void ReleaseWorkflowPublishesAndCommitsCanonicalManifest()
    {
        File.Exists(ProducerPath).ShouldBeTrue("The release history producer must be committed.");
        File.Exists(ContractPath).ShouldBeTrue("The producer/consumer contract must be documented.");

        var workflow = File.ReadAllText(WorkflowPath);
        workflow.ShouldContain("New-ReleaseHistoryManifest.ps1");
        workflow.ShouldContain("docs/public/releases/release-history.json");
        workflow.ShouldContain("steps.release_source.outputs.commit");
        workflow.ShouldContain("git add Directory.Build.props CHANGELOG.md docs/releases/ docs/public/releases/release-history.json");
    }

    [Fact]
    public void ReleaseWorkflowResolvesCommitBeforeMutatingReleaseArtifacts()
    {
        var workflow = File.ReadAllText(WorkflowPath);
        var resolveCommit = workflow.IndexOf("id: release_source", StringComparison.Ordinal);
        var bumpVersion = workflow.IndexOf("name: Bump version", StringComparison.Ordinal);
        var generateManifest = workflow.IndexOf("name: Generate release history manifest", StringComparison.Ordinal);
        var commitArtifacts = workflow.IndexOf("name: Commit release artifacts", StringComparison.Ordinal);

        resolveCommit.ShouldBeGreaterThanOrEqualTo(0);
        resolveCommit.ShouldBeLessThan(bumpVersion,
            "The manifest commit identity must be captured from the selected source before release-generated commits exist.");
        generateManifest.ShouldBeGreaterThan(bumpVersion);
        generateManifest.ShouldBeLessThan(commitArtifacts);
    }

    [Fact]
    public void ReleaseWorkflowPassesMatchingTagAndVersionIdentity()
    {
        var workflow = File.ReadAllText(WorkflowPath);
        var step = Regex.Match(
            workflow,
            @"(?ms)^ {6}- name: Generate release history manifest\s+.*?(?=^ {6}- name: )");

        step.Success.ShouldBeTrue("The release manifest generation step must exist.");
        step.Value.ShouldContain("Version = '${{ steps.version.outputs.version }}'");
        step.Value.ShouldContain("Tag = '${{ steps.version.outputs.tag }}'");
        step.Value.ShouldContain("Commit = '${{ steps.release_source.outputs.commit }}'");
        step.Value.ShouldContain("NotesPath = '/tmp/release-body.md'");
    }
}
