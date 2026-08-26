using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function requiring every GitHub Actions job to declare an explicit
/// <c>timeout-minutes</c> (#2513).
///
/// GitHub's default job timeout is <b>six hours</b>. A job that hangs - waiting on a lock, a
/// dead network dependency, or an interactive prompt that will never be answered - therefore
/// burns six hours of Actions time before the run is marked failed, and the PR sits red-pending
/// for a working day. On PR #2290 this happened twice on the same commit while a sibling
/// Playwright job on the same run finished in 1m04s, so the hang was job-local, not systemic.
///
/// A one-time edit adding timeouts drifts back the moment someone adds a new job, so this fence
/// enumerates every job in every committed workflow and fails when one omits the key.
/// </summary>
/// <remarks>
/// The fence is deliberately shape-only: it requires the key to be present with a positive
/// integer value, and does NOT dictate the number. Sizing is a maintainer judgement call and an
/// over-tight bound is worse than the bug it guards (a false timeout fails a legitimately slow
/// run). Anti-vacuity protections are mandatory here because a scanner that discovers zero files
/// is trivially green: the discovery count, the job count, and both detector polarities are all
/// asserted.
/// </remarks>
public sealed class WorkflowJobTimeoutArchitectureTests : ArchitectureTest
{
    /// <summary>Workflow files committed at the time this fence landed (#2513).</summary>
    private const int ExpectedWorkflowFileCount = 9;

    /// <summary>Jobs across those workflows at the time this fence landed (#2513).</summary>
    private const int ExpectedMinimumJobCount = 16;


    private string WorkflowsDir => Path.Combine(Repository.Root, ".github", "workflows");

    [Fact]
    public void Scan_DiscoversAllCommittedWorkflowFiles()
    {
        var files = WorkflowFiles();

        files.Count.ShouldBeGreaterThanOrEqualTo(ExpectedWorkflowFileCount,
            $"Anti-vacuity: expected at least {ExpectedWorkflowFileCount} workflow files under " +
            $"{WorkflowsDir} but found {files.Count}. A fence that scans nothing passes vacuously. " +
            "If workflows were legitimately removed, lower ExpectedWorkflowFileCount deliberately. " +
            "See issue #2513.");
    }

    [Fact]
    public void Scan_DiscoversAllJobsAcrossWorkflows()
    {
        var jobs = AllJobs();

        jobs.Count.ShouldBeGreaterThanOrEqualTo(ExpectedMinimumJobCount,
            $"Anti-vacuity: expected at least {ExpectedMinimumJobCount} jobs across the workflows " +
            $"but the parser enumerated only {jobs.Count} ({string.Join(", ", jobs.Select(j => j.Key))}). " +
            "Either jobs were removed or the YAML job parser has broken and the timeout fence is " +
            "now scanning nothing. See issue #2513.");
    }

    [Fact]
    public void EveryWorkflowJob_DeclaresTimeoutMinutes()
    {
        var offenders = AllJobs()
            .Where(j => ExtractTimeoutMinutes(j.Body) is null)
            .Select(j => j.Key)
            .ToList();

        offenders.ShouldBeEmpty(
            "These GitHub Actions jobs do not declare `timeout-minutes`, so a hang burns GitHub's " +
            "six-hour default before the run fails (#2513):\n  " +
            string.Join("\n  ", offenders) +
            "\nAdd an explicit `timeout-minutes:` to each job, sized with real headroom over its " +
            "normal runtime - e.g. immediately after `runs-on:`:\n" +
            "  jobs:\n    my-job:\n      runs-on: ubuntu-latest\n      timeout-minutes: 15\n" +
            $"Workflows directory: {WorkflowsDir}");
    }

    [Fact]
    public void EveryWorkflowJobTimeout_IsAPositiveInteger()
    {
        foreach (var job in AllJobs())
        {
            var raw = ExtractTimeoutMinutes(job.Body);
            if (raw is null)
            {
                // Covered by EveryWorkflowJob_DeclaresTimeoutMinutes; do not double-fail here.
                continue;
            }

            int.TryParse(raw, out var minutes).ShouldBeTrue(
                $"{job.Key} declares `timeout-minutes: {raw}` which is not an integer. GitHub " +
                "requires a number here. See issue #2513.");
            minutes.ShouldBeGreaterThan(0,
                $"{job.Key} declares a non-positive `timeout-minutes: {raw}`. See issue #2513.");
        }
    }

    // ---- non-vacuity pins: the detector must reject the broken shape and accept the fixed one ----

    [Fact]
    public void Fence_IsNotVacuous_DetectsJobWithoutTimeoutMinutes()
    {
        // Synthetic regression: the pre-#2513 shape - a job with no timeout at all.
        const string brokenYaml = """
            name: "CI: Build & Test"
            on:
              push:
                branches: [ main ]
            jobs:
              impacted-tests:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
            """;

        var jobs = ParseJobs(brokenYaml, "synthetic-broken.yml");

        jobs.Count.ShouldBe(1, "Vacuity guard: the parser must find the synthetic job at all.");
        ExtractTimeoutMinutes(jobs[0].Body).ShouldBeNull(
            "Vacuity guard: a job with no `timeout-minutes` must be detected as missing it. If this " +
            "returns a value the detector is too loose and the whole fence passes vacuously.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsJobWithTimeoutMinutes()
    {
        // Synthetic positive: the intended fixed shape. Must be accepted so the fence does not
        // over-tighten against the real (now-fixed) workflows.
        const string fixedYaml = """
            name: "CI: Build & Test"
            on:
              push:
                branches: [ main ]
            jobs:
              impacted-tests:
                runs-on: ubuntu-latest
                timeout-minutes: 30
                steps:
                  - uses: actions/checkout@v4
              full-tests:
                runs-on: ubuntu-latest
                timeout-minutes: 45
                steps:
                  - uses: actions/checkout@v4
            """;

        var jobs = ParseJobs(fixedYaml, "synthetic-fixed.yml");

        jobs.Count.ShouldBe(2, "Positive pin: the parser must enumerate both synthetic jobs.");
        ExtractTimeoutMinutes(jobs[0].Body).ShouldBe("30",
            "Positive pin: the fixed shape's timeout must be readable by the detector.");
        ExtractTimeoutMinutes(jobs[1].Body).ShouldBe("45",
            "Positive pin: the detector must read each job's own value, not the first job's.");
    }

    [Fact]
    public void Fence_ParserIgnoresStepLevelTimeoutMinutes()
    {
        // A step-level `timeout-minutes` bounds one step, NOT the job - it must not satisfy the
        // fence, otherwise a job could hang in any other step for six hours and still pass.
        const string stepOnlyYaml = """
            name: "trap"
            on: [push]
            jobs:
              sneaky:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - name: Test
                    timeout-minutes: 5
                    run: dotnet test
            """;

        var jobs = ParseJobs(stepOnlyYaml, "synthetic-step-only.yml");

        jobs.Count.ShouldBe(1, "Vacuity guard: the parser must find the synthetic job.");
        ExtractTimeoutMinutes(jobs[0].Body).ShouldBeNull(
            "A step-level `timeout-minutes` must NOT satisfy the job-level requirement - the job " +
            "could still hang in another step for the six-hour default. See issue #2513.");
    }

    // ---- helpers ----

    private sealed record WorkflowJob(string Key, string Body);

    private List<FileInfo> WorkflowFiles()
    {
        var dir = new DirectoryInfo(WorkflowsDir);
        dir.Exists.ShouldBeTrue($"Workflows directory not found: {WorkflowsDir}");

        return dir.EnumerateFiles("*.yml")
            .Concat(dir.EnumerateFiles("*.yaml"))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    private List<WorkflowJob> AllJobs()
    {
        return WorkflowFiles()
            .SelectMany(f => ParseJobs(File.ReadAllText(f.FullName), f.Name))
            .ToList();
    }

    /// <summary>
    /// Enumerates the direct children of the top-level <c>jobs:</c> mapping, returning each job's
    /// id (qualified with the file name for readable failure messages) and the raw text of its
    /// mapping body. Keys are matched structurally by indentation rather than by regex over the
    /// whole file, so nested keys (steps, matrix entries, service definitions) are never mistaken
    /// for jobs.
    /// </summary>
    private static List<WorkflowJob> ParseJobs(string yaml, string fileName)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var jobs = new List<WorkflowJob>();

        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^jobs\s*:\s*$"));
        if (start < 0)
        {
            return jobs;
        }

        // Indentation of the first job key determines the job level for this file.
        var jobIndent = -1;
        string? currentKey = null;
        var body = new List<string>();

        void Flush()
        {
            if (currentKey is not null)
            {
                jobs.Add(new WorkflowJob($"{fileName}:{currentKey}", string.Join("\n", body)));
            }

            body.Clear();
        }

        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                body.Add(line);
                continue;
            }

            // A column-zero key ends the jobs mapping.
            if (!char.IsWhiteSpace(line[0]))
            {
                break;
            }

            var indent = line.Length - line.TrimStart().Length;
            if (jobIndent < 0)
            {
                jobIndent = indent;
            }

            var keyMatch = Regex.Match(line, @"^\s*(?<k>[A-Za-z0-9_.\-]+)\s*:\s*$");
            if (indent == jobIndent && keyMatch.Success)
            {
                Flush();
                currentKey = keyMatch.Groups["k"].Value;
                continue;
            }

            body.Add(line);
        }

        Flush();
        return jobs;
    }

    /// <summary>
    /// Reads the job-level <c>timeout-minutes</c> value from a job body, or null when absent.
    /// Only keys at the job's own first mapping level count - a <c>timeout-minutes</c> nested
    /// under <c>steps:</c> bounds a single step and must not satisfy the job-level requirement.
    /// </summary>
    private static string? ExtractTimeoutMinutes(string jobBody)
    {
        var lines = jobBody.Replace("\r\n", "\n").Split('\n');
        var keyIndent = -1;

        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            if (keyIndent < 0)
            {
                keyIndent = indent;
            }

            if (indent != keyIndent)
            {
                continue;
            }

            var m = Regex.Match(line, @"^\s*timeout-minutes\s*:\s*(?<v>\S.*?)\s*$");
            if (m.Success)
            {
                return m.Groups["v"].Value.Trim();
            }
        }

        return null;
    }

}
