using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// AC2 fence: the BrowserTools guard layer CONSUMES the shared SSRF policy and never respells it.
/// </summary>
/// <remarks>
/// <para>
/// A behaviour test can prove the guard blocks <c>169.254.169.254</c>. It cannot prove HOW, and
/// "how" is the whole of AC2: a private copy of the range arithmetic would pass every behaviour
/// test in this repo and then quietly drift the day someone extends
/// <c>SsrfValidator</c> and forgets there is a second implementation. That is the exact
/// exemplar-fixed-never-propagated shape that produced #2761, #3013, #3018 and #3035.
/// </para>
/// <para>
/// Rule 1 - the project must reference <c>SsrfValidator</c> at least once, so "no duplicate
/// logic" cannot be satisfied by having no SSRF check at all.
/// </para>
/// <para>
/// Rule 2 - no file in the project may contain private-range, loopback or cloud-metadata
/// literals or address arithmetic of its own.
/// </para>
/// </remarks>
public sealed class BrowserToolsSsrfReuseArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string GuardProjectDirectory =>
        Path.Combine(RepoRoot, "src", "extensions", "BotNexus.Extensions.BrowserTools");

    /// <summary>
    /// Address literals and range tests that constitute "reimplementing SsrfValidator". Assembled
    /// from fragments so this file does not match its own patterns if it is ever scanned.
    /// </summary>
    private static readonly (string Name, Regex Pattern)[] DuplicateSsrfLogicPatterns =
    [
        ("link-local / IMDS literal", new Regex("169" + @"\." + "254", RegexOptions.Compiled)),
        ("loopback literal", new Regex("127" + @"\." + "0" + @"\." + "0" + @"\." + "1", RegexOptions.Compiled)),
        ("metadata host literal", new Regex("metadata" + @"\." + "google" + @"\." + "internal", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("RFC-1918 literal", new Regex(@"\b192\.168\.|\b172\.16\.|""10\.0\.0", RegexOptions.Compiled)),
        ("IPAddress range arithmetic", new Regex(@"GetAddressBytes|IPv6Loopback|IPAddress\.Loopback|IPAddress\.Any", RegexOptions.Compiled)),
        ("localhost blocklist", new Regex(@"""localhost""", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    ];

    [Fact]
    public void BrowserTools_ConsumesTheSharedSsrfValidator()
    {
        Directory.Exists(GuardProjectDirectory).ShouldBeTrue(
            $"the guard project must exist at {GuardProjectDirectory} for this fence to mean anything.");

        var sources = SourceFiles();
        sources.Length.ShouldBeGreaterThan(0, "the fence must have candidates to scan.");

        var callers = sources
            .Where(f => File.ReadAllText(f).Contains("SsrfValidator", StringComparison.Ordinal))
            .ToArray();

        callers.ShouldNotBeEmpty(
            "the BrowserTools guard must call the shared SsrfValidator. 'No duplicate logic' is "
            + "trivially satisfiable by having no SSRF validation at all, so this positive pin is "
            + "what stops the rule below from becoming a licence to drop the check.");
    }

    [Fact]
    public void BrowserTools_DoesNotReimplementPrivateRangeOrMetadataChecks()
    {
        var violations = (
            from file in SourceFiles()
            let text = StripComments(File.ReadAllText(file))
            from rule in DuplicateSsrfLogicPatterns
            where rule.Pattern.IsMatch(text)
            select $"{Path.GetFileName(file)}: {rule.Name}").ToArray();

        violations.ShouldBeEmpty(
            "BotNexus.Extensions.BrowserTools must delegate every private-range, loopback and "
            + "cloud-metadata decision to SsrfValidator in BotNexus.Gateway.Contracts. A second "
            + "copy passes its own tests right up until the shared policy is extended without it. "
            + "Violations: " + string.Join("; ", violations));
    }

    /// <summary>
    /// AC7: no test in the guard suite may launch a real browser or open a real socket. Asserted
    /// structurally rather than behaviourally because a test that DID launch something would
    /// still pass its own assertions - the harm is the subprocess and the outbound connection,
    /// not a failed expectation.
    /// </summary>
    [Fact]
    public void BrowserToolsTests_LaunchNoProcessAndOpenNoSocket()
    {
        var testDirectory = Path.Combine(
            RepoRoot, "tests", "extensions", "BotNexus.Extensions.BrowserTools.Tests");
        Directory.Exists(testDirectory).ShouldBeTrue($"expected the guard test project at {testDirectory}.");

        var files = Directory.GetFiles(testDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        files.Length.ShouldBeGreaterThan(0, "the fence must have candidates to scan.");

        var forbidden = new Regex(
            @"Process\.Start|ProcessStartInfo|new\s+HttpClient|HttpClientFactory|new\s+TcpClient|new\s+TcpListener|new\s+Socket\(",
            RegexOptions.Compiled);

        var violations = files
            .Where(f => forbidden.IsMatch(StripComments(File.ReadAllText(f))))
            .Select(Path.GetFileName)
            .ToArray();

        violations.ShouldBeEmpty(
            "guard tests must run entirely against the faked driver. Violations: "
            + string.Join("; ", violations));
    }

    private static string[] SourceFiles() =>
        Directory.Exists(GuardProjectDirectory)
            ? Directory.GetFiles(GuardProjectDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToArray()
            : [];

    /// <summary>
    /// Strips comments before scanning. Without this the fence fires on the guard's own doc
    /// comments explaining which addresses it deliberately does NOT check itself - the same trap
    /// the #2813 and #2955 fences hit.
    /// </summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*///.*$", string.Empty, RegexOptions.Multiline);
        source = Regex.Replace(source, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
        return source;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
