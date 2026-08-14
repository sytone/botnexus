using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function: every deployment template path referenced by
/// a tracked file under <c>infra/</c> or <c>docs/</c> must itself resolve to a
/// tracked file.
/// </summary>
/// <remarks>
/// <para>
/// This fence exists because of #3139. PR #3107 merged
/// <c>infra/buildtest/README-migration.md</c>, which instructs an operator to run
/// <c>az deployment group create --template-file infra/buildtest/network.bicep</c>
/// as the mandatory FIRST step of a one-way migration — but
/// <c>network.bicep</c> was never <c>git add</c>-ed. The documentation and the
/// <c>main.bicep</c> changes merged; the template the documentation depends on
/// did not exist in the repository at any commit. The only copy was an
/// untracked working-tree artefact and was subsequently destroyed with the
/// worktree, so it had to be re-authored from the procedure.
/// </para>
/// <para>
/// A dangling template reference is uniquely expensive: it is discovered by the
/// operator halfway through an irreversible infrastructure change, not by a
/// build. Enumerating tracked files with <c>git ls-files</c> — the same sweep
/// style as <see cref="PersonalPathLeakArchitectureTests"/> — makes "referenced
/// but never staged" a build failure instead of an outage.
/// </para>
/// </remarks>
public sealed class TemplateReferenceResolutionArchitectureTests
{
    // The fence's own source names the dangling-path shapes it hunts for
    // (and the probe fixture below deliberately contains one), so allowlist
    // them by basename.
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "TemplateReferenceResolutionArchitectureTests.cs",
    };

    // Placeholders an operator is expected to substitute, not real paths.
    private static readonly Regex Placeholder = new(
        @"[<>$%{}*]|\bYOUR\b|\bPATH_TO\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // `--template-file <path>` / `--template-file=<path>`, quoted or bare.
    private static readonly Regex TemplateFileFlag = new(
        "--template-file[=\\s]+[\"']?([^\"'\\s`]+)",
        RegexOptions.Compiled);

    // Any repo-relative-looking .bicep / .bicepparam path mentioned in prose or
    // script, e.g. `infra/buildtest/network.bicep`. A bare filename with no
    // directory segment (`main.bicep`) is NOT matched: it is ambiguous prose,
    // not a path assertion.
    private static readonly Regex QualifiedTemplatePath = new(
        @"(?<![A-Za-z0-9_./\\-])([A-Za-z0-9_.-]+(?:[/\\][A-Za-z0-9_.-]+)+\.bicep(?:param)?)\b",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ScannedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".ps1", ".psm1", ".sh", ".bicep", ".yml", ".yaml", ".json", ".txt",
    };

    [Fact]
    public void EveryReferencedTemplatePath_ResolvesToATrackedFile()
    {
        var repoRoot = FindRepoRoot();
        var tracked = EnumerateTrackedFiles(repoRoot)
            .Select(p => p.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offenders = new List<string>();

        foreach (var relative in ScannedFiles(tracked))
        {
            var absolute = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(absolute);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var reference in ExtractReferences(content))
            {
                if (Placeholder.IsMatch(reference))
                {
                    continue;
                }

                if (!Resolves(reference, relative, tracked))
                {
                    offenders.Add(
                        $"{relative}: references '{reference}', which is not a tracked file. " +
                        "Stage the template, or correct the reference.");
                }
            }
        }

        offenders.Sort(StringComparer.Ordinal);

        offenders.ShouldBeEmpty(
            "Tracked files reference deployment templates that do not exist in the repository. " +
            "This is the #3139 defect: documentation for a one-way infrastructure migration " +
            "shipped while the template it instructs the operator to deploy was never staged.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Non-vacuity guard: a broken enumeration (wrong repo root, git failure,
    /// over-narrow extension filter) would make the fence above pass by
    /// inspecting nothing. Assert the sweep sees a realistic corpus and that
    /// the reference extractor actually finds the known real references.
    /// </summary>
    [Fact]
    public void Sweep_InspectsANonTrivialCorpusAndFindsKnownReferences()
    {
        var repoRoot = FindRepoRoot();
        var tracked = EnumerateTrackedFiles(repoRoot)
            .Select(p => p.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scanned = ScannedFiles(tracked).ToList();
        scanned.Count.ShouldBeGreaterThan(
            30,
            $"Expected the infra/docs sweep to inspect a substantial file set; saw {scanned.Count}. " +
            "A collapsed count means the enumeration broke, not that the repo shrank.");

        var references = scanned
            .Select(rel => Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .SelectMany(abs => ExtractReferences(File.ReadAllText(abs)))
            .ToList();

        references.ShouldContain(
            r => r.EndsWith("infra/buildtest/network.bicep", StringComparison.OrdinalIgnoreCase),
            "The extractor must find the network.bicep reference in README-migration.md — " +
            "that is the exact reference #3139 was about.");
        references.ShouldContain(
            r => r.EndsWith("infra/buildtest/main.bicep", StringComparison.OrdinalIgnoreCase),
            "The extractor must find the main.bicep reference in README-migration.md.");
    }

    /// <summary>
    /// Proven-red probe: the resolution logic must REJECT a reference to a path
    /// that is not tracked, and ACCEPT one that is. Without this, a resolver
    /// that returned <c>true</c> unconditionally would leave the sweep green.
    /// </summary>
    [Fact]
    public void Resolver_RejectsDanglingReferenceAndAcceptsTrackedOne()
    {
        var repoRoot = FindRepoRoot();
        var tracked = EnumerateTrackedFiles(repoRoot)
            .Select(p => p.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        const string source = "infra/buildtest/README-migration.md";

        Resolves("infra/buildtest/network.bicep", source, tracked).ShouldBeTrue(
            "network.bicep is tracked on this branch; the resolver must accept it.");
        Resolves("infra/buildtest/does-not-exist.bicep", source, tracked).ShouldBeFalse(
            "A dangling template reference must be rejected — this is the whole point of the fence.");
    }

    private static IEnumerable<string> ScannedFiles(IEnumerable<string> tracked)
        => tracked.Where(p =>
            (p.StartsWith("infra/", StringComparison.OrdinalIgnoreCase) ||
             p.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)) &&
            !AllowedFiles.Contains(Path.GetFileName(p)) &&
            ScannedExtensions.Contains(Path.GetExtension(p)));

    private static IEnumerable<string> ExtractReferences(string content)
    {
        foreach (Match match in TemplateFileFlag.Matches(content))
        {
            yield return Normalise(match.Groups[1].Value);
        }
        foreach (Match match in QualifiedTemplatePath.Matches(content))
        {
            yield return Normalise(match.Groups[1].Value);
        }
    }

    private static string Normalise(string value) => value.Replace('\\', '/').Trim();

    /// <summary>
    /// A reference resolves if it names a tracked file either repo-relative
    /// (documentation is written to be run from the repo root) or relative to
    /// the directory of the file that mentions it (scripts commonly are).
    /// </summary>
    private static bool Resolves(string reference, string sourceRelativePath, HashSet<string> tracked)
    {
        if (tracked.Contains(reference))
        {
            return true;
        }

        var sourceDir = Path.GetDirectoryName(sourceRelativePath)?.Replace('\\', '/') ?? string.Empty;
        if (sourceDir.Length > 0)
        {
            var combined = Collapse($"{sourceDir}/{reference}");
            if (combined is not null && tracked.Contains(combined))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Collapse(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    private static IEnumerable<string> EnumerateTrackedFiles(string repoRoot)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", "ls-files")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        string? line;
        while ((line = process.StandardOutput.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, "git ls-files failed: " + process.StandardError.ReadToEnd());
    }

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
}
