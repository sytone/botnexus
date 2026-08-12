using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fence for issue #3012 AC5 — there must be exactly ONE scheme-validation helper
/// for MCP URLs, and no second copy in the contributor.
///
/// <para><b>Why a structural fence and not just behaviour tests.</b> The behavioural tests prove
/// the current call sites reject a plaintext credentialed URL. They cannot prove there is only one
/// definition of the rule. A future change that hand-rolls <c>scheme == "https"</c> inside
/// <c>McpToolContributor</c> — or inside a third call site — would keep every behaviour test green
/// while recreating exactly the drift this issue exists to close: two files each assuming the other
/// validated. The whole point of the fix is the seam, so the seam is what must be pinned.</para>
///
/// <para><b>Rule 1</b> — exactly one type in the MCP extension declares the scheme rule.</para>
/// <para><b>Rule 2</b> — no MCP source file outside that helper compares a URI scheme or tests
/// <c>IsLoopback</c> itself.</para>
/// <para><b>Rule 3</b> — no MCP source file outside the helper constructs a transport URI from raw
/// config with <c>new Uri(...)</c>, which is how the unvalidated path originally existed.</para>
/// <para><b>Rule 4</b> — both known credential-bearing call sites actually consume the helper, so
/// the fence cannot pass by the rule simply having no users.</para>
/// </summary>
public sealed class McpUrlSchemeValidationArchitectureTests
{
    private const string HelperTypeName = "McpUrlSecurity";

    private static string RepoRoot => FindRepoRoot();

    private static string McpSourceDirectory =>
        Path.Combine(RepoRoot, "src", "extensions", "BotNexus.Extensions.Mcp");

    [Fact]
    public void Mcp_extension_declares_exactly_one_scheme_validation_helper()
    {
        var files = EnumerateMcpSourceFiles();

        // Non-vacuity: the scan must actually see the extension's sources.
        files.Count.ShouldBeGreaterThan(5,
            $"Expected to scan the MCP extension sources under '{McpSourceDirectory}'. " +
            "A collapsed candidate set would make this fence pass without checking anything.");

        var declarations = files
            .Where(f => Regex.IsMatch(
                StripComments(File.ReadAllText(f)),
                $@"\b(class|static\s+class|record|struct)\s+{HelperTypeName}\b"))
            .Select(f => Path.GetRelativePath(RepoRoot, f))
            .ToList();

        declarations.Count.ShouldBe(1,
            $"Exactly one {HelperTypeName} must exist. Found: {string.Join(", ", declarations)}");
    }

    [Fact]
    public void No_mcp_source_outside_the_helper_hand_rolls_scheme_or_loopback_checks()
    {
        // Patterns that indicate a second, independent copy of the rule.
        var handRolled = new (string Name, Regex Pattern)[]
        {
            ("UriSchemeHttps comparison", new Regex(@"Uri\.UriSchemeHttps")),
            ("UriSchemeHttp comparison", new Regex(@"Uri\.UriSchemeHttp\b")),
            ("literal https scheme test", new Regex(@"""https""")),
            ("IsLoopback test", new Regex(@"\.IsLoopback")),
        };

        var offenders = new List<string>();

        foreach (var file in EnumerateMcpSourceFiles())
        {
            if (Path.GetFileNameWithoutExtension(file).Equals(HelperTypeName, StringComparison.Ordinal))
                continue;

            var source = StripComments(File.ReadAllText(file));

            foreach (var (name, pattern) in handRolled)
            {
                if (pattern.IsMatch(source))
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}: {name}");
            }
        }

        offenders.ShouldBeEmpty(
            $"MCP URL scheme validation must live only in {HelperTypeName}. A second copy is how " +
            "McpServerManager and McpToolContributor drifted apart in the first place (#3012). " +
            $"Offenders: {string.Join("; ", offenders)}");
    }

    [Fact]
    public void No_mcp_source_outside_the_helper_builds_a_uri_from_raw_config()
    {
        var newUri = new Regex(@"new\s+Uri\s*\(");
        var offenders = new List<string>();

        foreach (var file in EnumerateMcpSourceFiles())
        {
            if (Path.GetFileNameWithoutExtension(file).Equals(HelperTypeName, StringComparison.Ordinal))
                continue;

            if (newUri.IsMatch(StripComments(File.ReadAllText(file))))
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
        }

        offenders.ShouldBeEmpty(
            "The original defect was `new Uri(serverConfig.Url)` with no validation. MCP sources must " +
            $"obtain a validated endpoint from {HelperTypeName}.TryValidate instead. " +
            $"Offenders: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Both_credential_bearing_call_sites_consume_the_helper()
    {
        // Guards against the fence passing because nothing uses the rule at all.
        var callSites = new[]
        {
            Path.Combine(McpSourceDirectory, "McpServerManager.cs"),
            Path.Combine(McpSourceDirectory, "McpToolContributor.cs"),
        };

        foreach (var callSite in callSites)
        {
            File.Exists(callSite).ShouldBeTrue($"Expected call site '{callSite}' to exist.");

            StripComments(File.ReadAllText(callSite))
                .Contains($"{HelperTypeName}.", StringComparison.Ordinal)
                .ShouldBeTrue(
                    $"'{Path.GetFileName(callSite)}' injects or transmits credentials and must validate " +
                    $"the URL through {HelperTypeName}.");
        }
    }

    private static List<string> EnumerateMcpSourceFiles()
    {
        if (!Directory.Exists(McpSourceDirectory))
            return [];

        return Directory
            .EnumerateFiles(McpSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Removes comments and XML doc text before scanning. Without this the fence fires on the
    /// explanatory security comments the fix itself adds — which describe the rule rather than
    /// implementing it.
    /// </summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*///.*$", string.Empty, RegexOptions.Multiline);
        source = Regex.Replace(source, @"//.*$", string.Empty, RegexOptions.Multiline);
        return source;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, "BotNexus.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
