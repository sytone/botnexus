using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function: no tracked file in the repository may
/// contain personal paths, non-example email addresses, tenant-specific Azure
/// subscription IDs, or live public endpoint addresses. Private and operational
/// context must use generic placeholders or runtime configuration instead.
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
///   <item><description>Email addresses outside the approved <c>@domain.com</c>, <c>@example.com</c>, <c>@botnexus.invalid</c>, and <c>@invalid.local</c> domains</description></item>
///   <item><description>Concrete Azure subscription IDs in configuration or command contexts</description></item>
///   <item><description>Public IP addresses documented as current endpoints</description></item>
/// </list>
/// </remarks>
public sealed class PersonalPathLeakArchitectureTests : ArchitectureTest
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

    private static readonly Regex EmailAddress = new(
        @"\b[A-Z0-9._%+\-]*[A-Z0-9_%+]@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ConcreteAzureSubscription = new(
        @"(?:\|\s*Subscription\s*\||[\""']?subscriptionId[\""']?\s*[:=]|\$(?:sub|subscriptionId)\s*=)[^\r\n]{0,160}\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CurrentPublicIpAddress = new(
        @"Current\s+(?:static\s+)?IP[^\r\n]{0,80}\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

    [Fact]
    public void NoTrackedFile_ContainsNonExampleEmailAddress()
    {
        var offenders = ScanTrackedFiles((path, content) =>
            FindForbiddenEmail(content) is { } email
                ? $"{path}: contains email '{Truncate(email)}' — use an approved example domain"
                : null);

        offenders.ShouldBeEmpty(
            "Tracked files contain email addresses outside the approved @domain.com, @example.com, @botnexus.invalid, and @invalid.local domains. " +
            "Replace personal and organization-specific addresses with an approved example.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EmailFence_RecognizesOnlyActualNonExampleAddresses()
    {
        FindForbiddenEmail("Contact alice@domain.com").ShouldBeNull();

        var publicExample = "alice" + "@" + "example.com";
        FindForbiddenEmail("Contact " + publicExample).ShouldBeNull();

        FindForbiddenEmail("build-runner@botnexus.invalid").ShouldBeNull();
        FindForbiddenEmail("botnexus-test@invalid.local").ShouldBeNull();

        var corporateAddress = "alice" + "@" + "microsoft.com";
        FindForbiddenEmail("Contact " + corporateAddress).ShouldBe(corporateAddress);

        FindForbiddenEmail("git@github.com:owner/repository.git").ShouldBeNull();
        FindForbiddenEmail("https://user@example.com/path").ShouldBeNull();
    }

    [Fact]
    public void NoTrackedFile_ContainsConcreteAzureSubscriptionId()
    {
        var offenders = ScanTrackedFiles((path, content) =>
        {
            var match = ConcreteAzureSubscription.Match(content);
            return match.Success
                ? $"{path}: contains a concrete Azure subscription reference '{Truncate(match.Value)}' — use an environment variable"
                : null;
        });

        offenders.ShouldBeEmpty(
            "Tracked files contain concrete Azure subscription IDs in configuration or commands. " +
            "Use environment variables so tenant-specific identifiers cannot enter the repository.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoTrackedFile_ContainsCurrentPublicIpAddress()
    {
        var offenders = ScanTrackedFiles((path, content) =>
        {
            var match = CurrentPublicIpAddress.Match(content);
            return match.Success
                ? $"{path}: contains current public endpoint '{Truncate(match.Value)}' — show a lookup command instead"
                : null;
        });

        offenders.ShouldBeEmpty(
            "Tracked files contain current public IP addresses. Live endpoint metadata must be " +
            "queried at runtime rather than committed.\nOffenders:\n  " + string.Join("\n  ", offenders));
    }

    private List<string> ScanTrackedFiles(Func<string, string, string?> inspect)
    {
        var repoRoot = Repository.Root;
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

    private static string? FindForbiddenEmail(string content)
    {
        foreach (Match match in EmailAddress.Matches(content))
        {
            if (!IsExampleEmail(match.Value)
                && !IsUriAuthority(content, match)
                && !IsSshRemote(content, match))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static bool IsExampleEmail(string value)
        => value.EndsWith("@domain.com", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("@botnexus.invalid", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("@invalid.local", StringComparison.OrdinalIgnoreCase);

    private static bool IsUriAuthority(string content, Match match)
    {
        var lineStart = content.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
        var prefix = content[lineStart..match.Index];
        var schemeIndex = prefix.LastIndexOf("://", StringComparison.Ordinal);
        return schemeIndex >= 0 && !prefix[(schemeIndex + 3)..].Contains('/');
    }

    private static bool IsSshRemote(string content, Match match)
        => match.Index + match.Length < content.Length && content[match.Index + match.Length] == ':';

    private static string Truncate(string value)
        => value.Length <= 80 ? value : value[..80] + "...";
}
