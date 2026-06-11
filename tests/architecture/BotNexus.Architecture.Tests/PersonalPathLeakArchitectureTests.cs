using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function: no tracked file in the repository may
/// contain a developer-specific absolute path. Personal paths (Windows
/// user-home directories, OneDrive segments, Linux user-home directories)
/// are private context that should never reach a PR. Use generic
/// placeholders instead — <c>$HOME</c>, <c>~</c>, <c>%USERPROFILE%</c>,
/// <c>Path.GetTempPath()</c>, or <c>Environment.GetFolderPath(...)</c>.
/// </summary>
/// <remarks>
/// <para>
/// This fence exists because the first Phase 0b PR (#811) accidentally
/// shipped a hard-coded <c>C:/Users/&lt;alias&gt;/OneDrive/projects/captures</c>
/// path in the docstring of <c>scripts/dev/extract-copilot-fixtures.py</c>.
/// A regex sweep over every tracked file catches that class of leak before
/// it lands in another PR.
/// </para>
/// <para>
/// Patterns that fail the fence:
/// </para>
/// <list type="bullet">
///   <item><description><c>C:\Users\&lt;name&gt;\…</c> or <c>C:/Users/&lt;name&gt;/…</c> (any drive letter, any user name)</description></item>
///   <item><description>A path segment named <c>OneDrive</c> (e.g. <c>…/OneDrive/projects/…</c>)</description></item>
///   <item><description><c>/home/&lt;name&gt;/…</c> Linux user-home paths, except common CI accounts (<c>runner</c>, <c>vscode</c>, <c>codespace</c>, <c>circleci</c>)</description></item>
/// </list>
/// </remarks>
public sealed class PersonalPathLeakArchitectureTests
{
    // The test file itself contains the patterns it scans for. Allowlist it
    // by basename so the fence doesn't trip on its own documentation.
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "PersonalPathLeakArchitectureTests.cs",
    };

    // Linux user-home paths owned by CI runners or used as generic test /
    // documentation placeholders are not personal data. Keep this list short
    // — adding entries weakens the fence. Each entry must cite the file it
    // grandfathers.
    private static readonly HashSet<string> CiHomeAccounts = new(StringComparer.Ordinal)
    {
        "runner",     // GitHub Actions
        "vscode",     // VS Code dev container
        "codespace",  // GitHub Codespaces
        "circleci",   // CircleCI
        "agent",      // generic test fixture (tests/extensions/BotNexus.Extensions.Skills.Tests/SkillManagerToolTests.cs)
        "user",       // generic test fixture (tests/gateway/BotNexus.Gateway.Tests/PlatformConfigurationTests.cs)
        "you",        // generic docs placeholder for the reader (docs/guides/watchdog-setup.md)
        "larry",      // documented workflow persona, see fork: larry-fox-lobster/botnexus (.squad/skills/botnexus-issue-workflow/SKILL.md)
    };

    // Generic placeholder account names that appear in committed docs and
    // test fixtures. Same rationale as CiHomeAccounts: each entry must cite
    // the file that grandfathers it.
    private static readonly HashSet<string> GenericWindowsAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "username",   // generic docs placeholder (docs/development/workspace-and-memory.md)
        "test",       // generic test fixture (tests/gateway/BotNexus.Cron.Tests/CronOptionsPromptTemplateResolverTests.cs)
        "you",        // generic docs placeholder for the reader (docs/cli-reference.md)
    };

    // Built from fragments so the test source doesn't itself match the
    // Windows-user-home pattern. Matches "C:\Users\alice" or
    // "D:/Users/bob" (any drive letter, any user name) — captures the
    // user-name group so it can be checked against the allowlist.
    private static readonly Regex WindowsUserHome = new(
        "[A-Za-z]:" + @"[\\/]" + "[Uu]sers" + @"[\\/]" + "([A-Za-z0-9_.\\-]+)",
        RegexOptions.Compiled);

    private static readonly Regex OneDriveSegment = new(
        @"[\\/]" + "OneDrive" + @"[\\/]",
        RegexOptions.Compiled);

    private static readonly Regex LinuxUserHome = new(
        "/home/" + "([a-z][a-z0-9_-]*)/",
        RegexOptions.Compiled);

    [Fact]
    public void NoTrackedFile_ContainsWindowsUserHomePath()
    {
        var offenders = ScanTrackedFiles((path, content) =>
        {
            foreach (Match match in WindowsUserHome.Matches(content))
            {
                var account = match.Groups[1].Value;
                if (!GenericWindowsAccounts.Contains(account))
                {
                    return $"{path}: matched '{Truncate(match.Value)}' — use $HOME / %USERPROFILE% / Path.GetTempPath() instead";
                }
            }
            return null;
        });

        offenders.ShouldBeEmpty(
            "Tracked files contain personal Windows user-home paths (C:\\Users\\<name>\\... " +
            "or C:/Users/<name>/...). These leak developer identity into the repo. " +
            "Replace with $HOME, %USERPROFILE%, Path.GetTempPath(), or " +
            "Environment.GetFolderPath(SpecialFolder.UserProfile). The allowlist covers " +
            "generic placeholders only (username, test).\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoTrackedFile_ContainsOneDrivePathSegment()
    {
        var offenders = ScanTrackedFiles((path, content) =>
            OneDriveSegment.IsMatch(content)
                ? $"{path}: contains '/OneDrive/' segment — strip the cloud-sync prefix from documented paths"
                : null);

        offenders.ShouldBeEmpty(
            "Tracked files reference a OneDrive path segment. OneDrive is a developer-local " +
            "sync layout and must not appear in committed files.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoTrackedFile_ContainsPersonalLinuxHomePath()
    {
        var offenders = ScanTrackedFiles((path, content) =>
        {
            foreach (Match match in LinuxUserHome.Matches(content))
            {
                var account = match.Groups[1].Value;
                if (!CiHomeAccounts.Contains(account))
                {
                    return $"{path}: matched '/home/{account}/' — use $HOME or ~ instead";
                }
            }
            return null;
        });

        offenders.ShouldBeEmpty(
            "Tracked files contain personal Linux user-home paths (/home/<name>/...). " +
            "Replace with $HOME or ~. The allowlist covers common CI accounts only.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static List<string> ScanTrackedFiles(Func<string, string, string?> inspect)
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var relative in EnumerateTrackedFiles(repoRoot))
        {
            if (AllowedFiles.Contains(Path.GetFileName(relative)))
            {
                continue;
            }
            if (!IsTextFile(relative))
            {
                continue;
            }

            var absolute = Path.Combine(repoRoot, relative);
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

            var result = inspect(relative.Replace('\\', '/'), content);
            if (result is not null)
            {
                offenders.Add(result);
            }
        }

        offenders.Sort(StringComparer.Ordinal);
        return offenders;
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

    // Skip binary file extensions where personal paths can't reasonably be
    // searched and would only slow the test. .mitm, .pptx, images, etc.
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp",
        ".pdf", ".zip", ".gz", ".tar", ".7z", ".dll", ".exe", ".pdb",
        ".mitm", ".pptx", ".docx", ".xlsx", ".woff", ".woff2", ".ttf",
        ".eot", ".otf", ".mp3", ".mp4", ".wav", ".mov",
    };

    private static bool IsTextFile(string relativePath)
        => !BinaryExtensions.Contains(Path.GetExtension(relativePath));

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

    private static string Truncate(string value)
        => value.Length <= 80 ? value : value[..80] + "...";
}
