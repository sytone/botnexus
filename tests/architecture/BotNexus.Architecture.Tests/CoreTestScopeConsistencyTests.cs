using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// The "core" test scope is spelled in three places: the CI pull-request job, the CI push job, and
/// the remote container runner's entrypoint. They must agree, because a gate that runs a different
/// set from the one it claims is worse than no gate -- it reports confidence it has not earned.
///
/// This is a real risk rather than a hypothetical one. The push job previously carried its own
/// hand-written exclusion list that had drifted to also skip BotNexus.Integration.* and
/// BotNexus.Conversation.*, so those projects never ran in CI at all while the job reported green.
/// A comment asking future editors to keep them in sync is a wish; this fails the build instead.
/// </summary>
public class CoreTestScopeConsistencyTests : ArchitectureTest
{
    private const string CoreFilter =
        "FullyQualifiedName!~BotNexus.Integration.E2E&FullyQualifiedName!~BotNexus.E2E";


    [Fact]
    public void CoreScope_IsSpelledIdentically_InCiAndTheRemoteRunner()
    {
        var root = Repository.Root;
        var sources = new Dictionary<string, string>
        {
            [".github/workflows/ci-build-test.yml"] = Path.Combine(root, ".github", "workflows", "ci-build-test.yml"),
            ["infra/buildtest/runner/entrypoint.ps1"] = Path.Combine(root, "infra", "buildtest", "runner", "entrypoint.ps1"),
        };

        foreach (var (label, path) in sources)
        {
            Assert.True(File.Exists(path), $"Expected {label} to exist at {path}.");
            var text = File.ReadAllText(path);
            Assert.True(
                text.Contains(CoreFilter, StringComparison.Ordinal),
                $"{label} does not contain the canonical core filter. Every definition of the core " +
                $"scope must match exactly, otherwise CI and the remote gate silently run different " +
                $"test sets. Expected to find: {CoreFilter}");
        }
    }

    [Fact]
    public void CiWorkflow_DoesNotExcludeProjectsBeyondTheCoreScope()
    {
        var workflow = Path.Combine(Repository.Root, ".github", "workflows", "ci-build-test.yml");
        var text = File.ReadAllText(workflow);

        // Any FullyQualifiedName!~ exclusion must name an E2E project. Excluding anything else
        // removes a project from the gate entirely, which is how Integration and Conversation
        // stopped running without anyone noticing.
        var exclusions = Regex.Matches(text, @"FullyQualifiedName!~(?<target>[A-Za-z0-9_.]+)")
            .Select(m => m.Groups["target"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(exclusions);

        var unexpected = exclusions
            .Where(e => !e.Contains("E2E", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            "The CI workflow excludes projects outside the agreed core scope: " +
            string.Join(", ", unexpected) +
            ". Only E2E/browser projects may be excluded; they are quarantined because a fixture " +
            "crash reported 265 of 280 tests NotExecuted while the run still exited 0.");
    }
}
