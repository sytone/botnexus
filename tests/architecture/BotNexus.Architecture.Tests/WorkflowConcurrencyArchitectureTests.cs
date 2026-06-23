using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function guarding GitHub Actions workflow concurrency groups (#1548).
///
/// Without a top-level <c>concurrency:</c> block, a rapid sequence of pushes to <c>main</c> or a
/// PR <c>synchronize</c> event spawns a fresh workflow run per push while the prior runs keep
/// executing. For <c>security-codeql.yml</c> that means stacked CodeQL analyses burning Actions
/// minutes and queue; for the redundant build/integration CI it means stacked builds on the
/// fast-moving maintenance-PR branches. Only <c>deploy-docs.yml</c> historically set a group.
///
/// The fix (mirroring the OpenClaw <c>d491e9c69bb9</c> / <c>5cf8ba973d27</c> precedent) adds a
/// per-ref <c>concurrency.group</c> to each workflow:
/// <list type="bullet">
///   <item>Security scans (<c>security-codeql.yml</c>) keep <c>cancel-in-progress: false</c> — a
///   scan should COMPLETE, never be cancelled mid-run; only the per-ref grouping changes.</item>
///   <item>Redundant CI (<c>ci-build-test.yml</c>, <c>ci-container-integration.yml</c>) may cancel
///   superseded in-progress runs so the latest commit wins instead of stacking.</item>
/// </list>
///
/// This is CI-config that GitHub itself enforces at dispatch time; a static text fitness function
/// is the right regression guard because CI cannot dry-run its own concurrency semantics. The
/// fence parses the committed YAML — zero Actions dependency.
/// </summary>
/// <remarks>
/// The fence is intentionally narrow: it requires a top-level <c>concurrency:</c> block with a
/// <c>group:</c> key on each named workflow, requires the per-ref key to vary by ref (so all refs
/// do not collapse into one global group), and for the security scan requires
/// <c>cancel-in-progress: false</c>. It does not dictate the exact group-key expression beyond
/// referencing <c>github.ref</c> / <c>github.head_ref</c>, leaving room for the maintainer to tune.
/// </remarks>
public sealed class WorkflowConcurrencyArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string WorkflowsDir => Path.Combine(RepoRoot, ".github", "workflows");

    // Workflows that MUST carry a per-ref concurrency group. (deploy-docs.yml already has one and
    // is not in scope here; release-cli.yml / security-secrets-deps.yml are intentionally left to
    // complete and are not asserted by this fence.)
    private static readonly string[] GroupedWorkflows =
    {
        "security-codeql.yml",
        "ci-build-test.yml",
        "ci-container-integration.yml",
    };

    // The security scan must never auto-cancel mid-run: a partial CodeQL analysis is worse than a
    // queued one. Only the grouping/isolation changes, not the to-completion guarantee.
    private const string SecurityScanWorkflow = "security-codeql.yml";

    [Fact]
    public void TargetWorkflows_Exist()
    {
        foreach (var wf in GroupedWorkflows)
        {
            File.Exists(Path.Combine(WorkflowsDir, wf)).ShouldBeTrue(
                $"Expected workflow {wf} not found under {WorkflowsDir}");
        }
    }

    [Fact]
    public void TargetWorkflows_DeclareTopLevelConcurrencyGroup()
    {
        foreach (var wf in GroupedWorkflows)
        {
            var yaml = File.ReadAllText(Path.Combine(WorkflowsDir, wf));

            HasTopLevelConcurrencyGroup(yaml).ShouldBeTrue(
                $"{wf} has no top-level `concurrency:` block with a `group:` key. Rapid pushes / PR " +
                "synchronize events will stack overlapping runs (stale CodeQL/CI burning Actions " +
                "minutes and queue). Add a top-level concurrency block keyed per ref, e.g.\n" +
                "  concurrency:\n" +
                "    group: ${{ github.workflow }}-${{ github.event_name == 'pull_request' && github.head_ref || github.ref }}\n" +
                "    cancel-in-progress: <true for redundant CI | false for security scans>\n" +
                "See issue #1548.\nFile: " + Path.Combine(WorkflowsDir, wf));
        }
    }

    [Fact]
    public void TargetWorkflows_ConcurrencyGroupIsPerRef_NotGlobal()
    {
        foreach (var wf in GroupedWorkflows)
        {
            var yaml = File.ReadAllText(Path.Combine(WorkflowsDir, wf));
            var group = ExtractConcurrencyGroupValue(yaml);

            group.ShouldNotBeNull(
                $"{wf} concurrency block has no readable `group:` value. See issue #1548.");

            ConcurrencyGroupVariesByRef(group!).ShouldBeTrue(
                $"{wf} concurrency `group:` ('{group}') does not vary by ref — it must reference " +
                "`github.ref`, `github.head_ref`, or `github.run_id` so per-ref runs are isolated and " +
                "unrelated refs do not collapse into one global serialized group (which would block " +
                "parallel PRs). See issue #1548.\nFile: " + Path.Combine(WorkflowsDir, wf));
        }
    }

    [Fact]
    public void SecurityScan_KeepsCancelInProgressFalse()
    {
        var path = Path.Combine(WorkflowsDir, SecurityScanWorkflow);
        var yaml = File.ReadAllText(path);

        var cancel = ExtractCancelInProgressValue(yaml);

        cancel.ShouldNotBeNull(
            $"{SecurityScanWorkflow} concurrency block has no `cancel-in-progress:` key. A security " +
            "scan must explicitly set `cancel-in-progress: false` so it always runs to completion " +
            "(a half-finished CodeQL analysis is worse than a queued one). See issue #1548.");

        cancel!.Trim().ToLowerInvariant().ShouldBe("false",
            $"{SecurityScanWorkflow} must keep `cancel-in-progress: false` — security scans should " +
            "COMPLETE, not be cancelled mid-run. Only the per-ref grouping changes. " +
            "(Per OpenClaw d491e9c69bb9 precedent.) See issue #1548.\nFile: " + path);
    }

    // ---- non-vacuity guards: the detectors must flag the broken shape and accept the fixed one ----

    [Fact]
    public void Fence_IsNotVacuous_DetectsMissingConcurrencyBlock()
    {
        // Synthetic regression: the pre-#1548 shape — a workflow with no concurrency block at all.
        const string brokenYaml = """
            name: "Security: CodeQL Analysis"
            on:
              push:
                branches: [ "main" ]
              pull_request:
                branches: [ "main" ]
            jobs:
              analyze:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
            """;

        HasTopLevelConcurrencyGroup(brokenYaml).ShouldBeFalse(
            "Vacuity guard: a workflow with no concurrency block must be detected as missing one. " +
            "If this passes, the detector is too loose and the fence passes vacuously.");
        ExtractConcurrencyGroupValue(brokenYaml).ShouldBeNull(
            "Vacuity guard: there is no group value to extract from the broken shape.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsPerRefSecurityScanWithCancelFalse()
    {
        // Synthetic positive: the intended fixed shape for the security scan. Must be accepted so
        // the fence does not over-tighten against the real (now-fixed) workflow.
        const string fixedYaml = """
            name: "Security: CodeQL Analysis"
            on:
              push:
                branches: [ "main" ]
              pull_request:
                branches: [ "main" ]
            concurrency:
              group: codeql-${{ github.workflow }}-${{ github.event_name == 'pull_request' && github.head_ref || github.ref }}
              cancel-in-progress: false
            jobs:
              analyze:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
            """;

        HasTopLevelConcurrencyGroup(fixedYaml).ShouldBeTrue(
            "Positive pin: the intended fixed shape must be accepted by the group detector.");
        var group = ExtractConcurrencyGroupValue(fixedYaml);
        ConcurrencyGroupVariesByRef(group!).ShouldBeTrue(
            "Positive pin: the fixed group key references github.head_ref/github.ref and must be " +
            "detected as per-ref.");
        ExtractCancelInProgressValue(fixedYaml)!.Trim().ToLowerInvariant().ShouldBe("false",
            "Positive pin: the fixed security-scan shape keeps cancel-in-progress false.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsRedundantCiCancelTrue()
    {
        // Redundant CI may cancel superseded PR runs. The group detector and per-ref detector must
        // accept a cancel-in-progress: true shape too (the fence only PINS cancel=false for the scan).
        const string fixedCiYaml = """
            name: "CI: Build & Test"
            on:
              push:
                branches: [ main ]
              pull_request:
                branches: [ main ]
            concurrency:
              group: ${{ github.workflow }}-${{ github.event_name == 'pull_request' && github.head_ref || github.ref }}
              cancel-in-progress: ${{ github.event_name == 'pull_request' }}
            permissions:
              contents: read
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
            """;

        HasTopLevelConcurrencyGroup(fixedCiYaml).ShouldBeTrue(
            "Positive pin: redundant-CI fixed shape must be accepted by the group detector.");
        ConcurrencyGroupVariesByRef(ExtractConcurrencyGroupValue(fixedCiYaml)!).ShouldBeTrue(
            "Positive pin: redundant-CI group key is per-ref.");
    }

    // ---- helpers ----

    /// <summary>
    /// True when the YAML has a top-level (column-zero) <c>concurrency:</c> mapping that contains a
    /// <c>group:</c> key. A nested job-level concurrency block does not satisfy this.
    /// </summary>
    private static bool HasTopLevelConcurrencyGroup(string yaml)
    {
        return ExtractConcurrencyBlock(yaml) is { } block &&
               Regex.IsMatch(block, @"(?m)^\s+group\s*:");
    }

    private static string? ExtractConcurrencyGroupValue(string yaml)
    {
        var block = ExtractConcurrencyBlock(yaml);
        if (block is null)
        {
            return null;
        }

        var m = Regex.Match(block, @"(?m)^\s+group\s*:\s*(?<v>\S.*?)\s*$");
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    private static string? ExtractCancelInProgressValue(string yaml)
    {
        var block = ExtractConcurrencyBlock(yaml);
        if (block is null)
        {
            return null;
        }

        var m = Regex.Match(block, @"(?m)^\s+cancel-in-progress\s*:\s*(?<v>\S.*?)\s*$");
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    /// <summary>
    /// Returns the text of the top-level <c>concurrency:</c> mapping (its indented child lines),
    /// or null if there is no column-zero <c>concurrency:</c> key. Stops at the next column-zero key.
    /// </summary>
    private static string? ExtractConcurrencyBlock(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            // Top-level key = no leading whitespace, exactly `concurrency:`.
            if (Regex.IsMatch(lines[i], @"^concurrency\s*:\s*$"))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var collected = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                collected.Add(line);
                continue;
            }

            // A new column-zero (non-whitespace-led) key ends the block.
            if (!char.IsWhiteSpace(line[0]))
            {
                break;
            }

            collected.Add(line);
        }

        return string.Join("\n", collected);
    }

    /// <summary>
    /// A concurrency group varies by ref when its expression references one of the per-ref context
    /// values. A constant like <c>group: pages</c> would NOT vary by ref and would serialize all refs.
    /// </summary>
    private static bool ConcurrencyGroupVariesByRef(string groupValue)
    {
        return groupValue.Contains("github.ref", StringComparison.OrdinalIgnoreCase) ||
               groupValue.Contains("github.head_ref", StringComparison.OrdinalIgnoreCase) ||
               groupValue.Contains("github.run_id", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root (BotNexus.slnx) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
