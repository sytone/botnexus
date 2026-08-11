using System.Diagnostics;
using System.Text;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Non-vacuity and behaviour tests for <c>scripts/repo/docs-lint.ps1</c> (issue #2865).
/// </summary>
/// <remarks>
/// <para>
/// The point of a lint gate is that it FAILS on the defect that motivated it. A lint that
/// cannot go red on the twelve documentation defects found on 2026-08-07 is decoration, so
/// every rule here is pinned against a fixture reproducing its motivating defect
/// (issue #2865 AC2/AC3/AC4) AND against a corrected fixture, so it cannot pass by flagging
/// everything either.
/// </para>
/// <para>
/// Each test builds a synthetic mini-repo on disk rather than pointing at <c>docs/</c>. Pinning
/// the real docset would make the fixture rot the moment someone edits a page, and the
/// pre-fix defect commits are already merged away - the defect must be reproducible from a
/// fixture or it is not reproducible at all.
/// </para>
/// </remarks>
public sealed class DocsLintScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "docs-lint-tests-" + Guid.NewGuid().ToString("N"));

    // The lint refuses to certify a docset it barely read; fixtures must clear that floor.
    private const int MinimumFixtureDocs = 21;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test run over.
        }
    }

    // -----------------------------------------------------------------------
    // Rule 1 - literal drift. AC2: fails on the 18790 defect.
    // -----------------------------------------------------------------------

    [Fact]
    public void LiteralDrift_FailsOnThePortThatAppearsOnlyInDocs()
    {
        var repo = NewFixtureRepo();
        // The exact shape of the getting-started-release.md defect: a fenced instruction
        // pointing the reader at a port that exists nowhere in source.
        WriteDoc(repo, "getting-started-release.md",
            "# Getting started\n\nOpen the portal:\n\n```\nhttp://localhost:18790\n```\n");

        var result = RunLint(repo, "literal-drift");

        result.ExitCode.ShouldBe(1, "the 18790 defect must make docs-lint go red:\n" + result.Output);
        result.Output.ShouldContain("18790");
        result.Output.ShouldContain("getting-started-release.md");
    }

    [Fact]
    public void LiteralDrift_PassesOnThePortSourceActuallyDeclares()
    {
        var repo = NewFixtureRepo();
        WriteDoc(repo, "getting-started-release.md",
            "# Getting started\n\nOpen the portal:\n\n```\nhttp://localhost:5005\n```\n");

        var result = RunLint(repo, "literal-drift");

        result.ExitCode.ShouldBe(0,
            "the corrected port is declared in the fixture's source tree, so the rule must stay green:\n"
            + result.Output);
    }

    [Fact]
    public void LiteralDrift_IgnoresAStalePortMentionedOnlyInProse()
    {
        var repo = NewFixtureRepo();
        // Prose may legitimately discuss a retired port ("the old port was 18790").
        // Only a fenced block is an instruction the reader copies.
        WriteDoc(repo, "history.md",
            "# History\n\nBefore #2798 the portal listened on localhost:18790. It no longer does.\n");

        var result = RunLint(repo, "literal-drift");

        result.ExitCode.ShouldBe(0,
            "a retired port discussed in prose is not an instruction and must not fail the gate:\n"
            + result.Output);
    }

    [Fact]
    public void LiteralDrift_FailsOnAConfigKeyTheBinderNoLongerReads()
    {
        var repo = NewFixtureRepo();
        WriteDoc(repo, "cron-config.md",
            "# Cron config\n\n```json\n{ \"BotNexus.Cron.Jobs\": [] }\n```\n");

        var result = RunLint(repo, "literal-drift");

        result.ExitCode.ShouldBe(1, "a dotted config key absent from source must go red:\n" + result.Output);
        result.Output.ShouldContain("BotNexus.Cron.Jobs");
    }

    [Fact]
    public void LiteralDrift_RespectsTheAllowList()
    {
        var repo = NewFixtureRepo();
        WriteDoc(repo, "external.md", "# External\n\n```\nhttp://localhost:18790\n```\n");
        File.WriteAllText(
            Path.Combine(repo, "scripts", "repo", "docs-lint-allow.json"),
            "{ \"ports\": [\"18790\"], \"keys\": [] }");

        var result = RunLint(repo, "literal-drift");

        result.ExitCode.ShouldBe(0,
            "an explicitly justified docs-only literal must be suppressible:\n" + result.Output);
    }

    // -----------------------------------------------------------------------
    // Rule 2 - intra-page contradiction. AC3: fails on tickIntervalSeconds 60-vs-10.
    // -----------------------------------------------------------------------

    [Fact]
    public void Contradiction_FailsOnTheTickIntervalSixtyVersusTenPage()
    {
        var repo = NewFixtureRepo();
        // Verbatim shape of the cron-and-scheduling.md defect: a table says 60,
        // a diagram further down says 10.
        WriteDoc(repo, "cron-and-scheduling.md",
            "# Cron\n\n| Setting | Default |\n| --- | --- |\n"
            + "| tickIntervalSeconds | 60 |\n\n"
            + "## How it ticks\n\nThe scheduler wakes on tickIntervalSeconds = 10 and re-evaluates.\n");

        var result = RunLint(repo, "intra-page-contradiction");

        result.ExitCode.ShouldBe(1,
            "a page stating two different defaults for the same fact must go red:\n" + result.Output);
        result.Output.ShouldContain("cronTickIntervalSeconds");
        result.Output.ShouldContain("cron-and-scheduling.md");
    }

    [Fact]
    public void Contradiction_PassesWhenThePageAgreesWithItself()
    {
        var repo = NewFixtureRepo();
        WriteDoc(repo, "cron-and-scheduling.md",
            "# Cron\n\n| Setting | Default |\n| --- | --- |\n"
            + "| tickIntervalSeconds | 60 |\n\n"
            + "## How it ticks\n\nThe scheduler wakes on tickIntervalSeconds = 60 and re-evaluates.\n");

        var result = RunLint(repo, "intra-page-contradiction");

        result.ExitCode.ShouldBe(0,
            "restating the same value twice is consistency, not contradiction:\n" + result.Output);
    }

    [Fact]
    public void Contradiction_IsScopedToASinglePage()
    {
        var repo = NewFixtureRepo();
        // Two pages, two values. Cross-page divergence is a different (and much noisier)
        // problem; rule 2 deliberately scopes to one page so it can be a HARD failure.
        WriteDoc(repo, "page-a.md", "# A\n\ntickIntervalSeconds = 60\n");
        WriteDoc(repo, "page-b.md", "# B\n\ntickIntervalSeconds = 10\n");

        var result = RunLint(repo, "intra-page-contradiction");

        result.ExitCode.ShouldBe(0,
            "rule 2 is an intra-page rule; two pages disagreeing is out of its scope:\n" + result.Output);
    }

    [Fact]
    public void Contradiction_FailsOnAPageQuotingTwoDifferentGatewayPorts()
    {
        var repo = NewFixtureRepo();
        WriteDoc(repo, "ports.md",
            "# Ports\n\nBrowse to http://localhost:5005 for the portal.\n\n"
            + "Then open http://localhost:18790 to finish setup.\n");

        var result = RunLint(repo, "intra-page-contradiction");

        result.ExitCode.ShouldBe(1,
            "one page giving two different gateway ports is exactly the trust-destroying shape:\n"
            + result.Output);
        result.Output.ShouldContain("gatewayLoopbackPort");
    }

    // -----------------------------------------------------------------------
    // Rule 3 - legacy marker. AC4: fails on the pre-fix provider how-to.
    // -----------------------------------------------------------------------

    [Fact]
    public void LegacyMarker_FailsWhenTheCaveatFollowsTheSample()
    {
        var repo = NewFixtureRepo();
        // Shape of the extension-development.md defect: a how-to heading, a copyable
        // sample, and only afterwards the disclosure that the base class is legacy.
        WriteDoc(repo, "extension-development.md",
            "# Extensions\n\n## Implementing a provider\n\n"
            + "```csharp\npublic class MyProvider : LlmProviderBase { }\n```\n\n"
            + "Note: LlmProviderBase is legacy and non-functional; implement IApiProvider instead.\n");

        var result = RunLint(repo, "legacy-marker");

        result.ExitCode.ShouldBe(1,
            "a legacy disclosure placed below the sample must go red:\n" + result.Output);
        result.Output.ShouldContain("extension-development.md");
    }

    [Fact]
    public void LegacyMarker_PassesWhenTheCaveatBannersTheSample()
    {
        var repo = NewFixtureRepo();
        WriteDoc(repo, "extension-development.md",
            "# Extensions\n\n## Implementing a provider\n\n"
            + "> **Legacy - do not copy.** LlmProviderBase is non-functional; implement IApiProvider.\n\n"
            + "```csharp\npublic class MyProvider : LlmProviderBase { }\n```\n");

        var result = RunLint(repo, "legacy-marker");

        result.ExitCode.ShouldBe(0,
            "the banner-first form is the ACCEPTED shape and must not be flagged:\n" + result.Output);
    }

    [Fact]
    public void LegacyMarker_IgnoresNonHowToSections()
    {
        var repo = NewFixtureRepo();
        WriteDoc(repo, "migration-notes.md",
            "# Migration\n\n## Background\n\n```csharp\nvar x = 1;\n```\n\nThis API is deprecated.\n");

        var result = RunLint(repo, "legacy-marker");

        result.ExitCode.ShouldBe(0,
            "a reference/background section is not instructing the reader to copy anything:\n"
            + result.Output);
    }

    // -----------------------------------------------------------------------
    // Gate-level anti-vacuity.
    // -----------------------------------------------------------------------

    [Fact]
    public void Lint_RefusesToCertifyADocsetItBarelyRead()
    {
        var repo = NewFixtureRepo(docCount: 3);

        var result = RunLint(repo, "literal-drift");

        result.ExitCode.ShouldBe(2,
            "a sweep that inspects almost nothing must fail as a usage error, never pass green:\n"
            + result.Output);
        result.Output.ShouldContain("vacuous");
    }

    [Fact]
    public void Lint_EmitsMachineReadableJsonOnStdout()
    {
        var repo = NewFixtureRepo();
        WriteDoc(repo, "getting-started-release.md", "# Start\n\n```\nhttp://localhost:18790\n```\n");

        var result = RunLint(repo, "literal-drift", asJson: true);

        result.ExitCode.ShouldBe(1);
        // stdout must be parseable with no scraping (wrapper stdout purity, #2420/#2761).
        var json = System.Text.Json.JsonDocument.Parse(result.StdOut.Trim());
        json.RootElement.GetProperty("findingCount").GetInt32().ShouldBeGreaterThan(0);
        json.RootElement.GetProperty("findings")[0].GetProperty("rule").GetString()
            .ShouldBe("literal-drift");
    }

    [Fact]
    public void Lint_IsCleanAgainstTheRealDocset()
    {
        // The gate is only wireable into CI if the tree it guards currently passes.
        var repoRoot = FindRepoRoot();
        var result = RunLintAt(repoRoot, Path.Combine(repoRoot, "scripts", "repo", "docs-lint.ps1"),
            "literal-drift,intra-page-contradiction,legacy-marker", asJson: false);

        result.ExitCode.ShouldBe(0,
            "docs-lint must be green on main, otherwise it cannot be a required check:\n" + result.Output);
    }

    // -----------------------------------------------------------------------
    // Fixture helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a minimal repo: a source tree declaring the real loopback port, the lint
    /// script plus its registries copied from the repo under test, and enough filler
    /// documentation pages to clear the lint's own anti-vacuity floor.
    /// </summary>
    private string NewFixtureRepo(int docCount = MinimumFixtureDocs)
    {
        var repo = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        Directory.CreateDirectory(Path.Combine(repo, "scripts", "repo"));

        File.WriteAllText(
            Path.Combine(repo, "src", "GatewayBindAddress.cs"),
            "public static class GatewayBindAddress\n{\n"
            + "    public const string LoopbackListenUrl = \"http://localhost:5005\";\n}\n");

        var realRoot = FindRepoRoot();
        foreach (var name in new[] { "docs-lint.ps1", "docs-lint-facts.json", "docs-lint-allow.json" })
        {
            File.Copy(
                Path.Combine(realRoot, "scripts", "repo", name),
                Path.Combine(repo, "scripts", "repo", name),
                overwrite: true);
        }

        for (var i = 0; i < docCount; i++)
        {
            File.WriteAllText(
                Path.Combine(repo, "docs", $"filler-{i}.md"),
                $"# Filler {i}\n\nNothing interesting here.\n");
        }

        return repo;
    }

    private static void WriteDoc(string repo, string relativeName, string content)
        => File.WriteAllText(Path.Combine(repo, "docs", relativeName), content);

    private static LintRun RunLint(string repo, string rules, bool asJson = false)
        => RunLintAt(repo, Path.Combine(repo, "scripts", "repo", "docs-lint.ps1"), rules, asJson);

    private static LintRun RunLintAt(string repoRoot, string scriptPath, string rules, bool asJson)
    {
        var args = new StringBuilder();
        args.Append("-NoProfile -NonInteractive -File \"").Append(scriptPath).Append('"');
        args.Append(" -RepoRoot \"").Append(repoRoot).Append('"');
        args.Append(" -Rule ").Append(rules);
        if (asJson)
        {
            args.Append(" -AsJson");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(PwshExecutable(), args.ToString())
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new LintRun(process.ExitCode, stdout, stderr);
    }

    private static string PwshExecutable()
        => OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        return current.FullName;
    }

    private sealed record LintRun(int ExitCode, string StdOut, string StdErr)
    {
        public string Output => StdOut + StdErr;
    }
}
