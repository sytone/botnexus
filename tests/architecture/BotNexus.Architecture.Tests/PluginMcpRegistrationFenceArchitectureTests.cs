using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fence for #2686: plugin-declared MCP servers go through the EXISTING server manager, and the
/// plugin trust vocabulary stays identical to the skills one.
/// </summary>
/// <remarks>
/// <para>
/// A behaviour test can prove a plugin's server starts. It cannot prove it started through
/// <c>McpServerManager</c> rather than through a second, plugin-only implementation that happens to
/// behave the same today. The parallel-registry failure is precisely the one the issue names, and it
/// is invisible to every behaviour test until the two lifecycles diverge - typically when one of
/// them forgets to tear a process down.
/// </para>
/// <para>
/// Rule 1 - the plugin registration seam must reference <c>McpServerManager</c> at least once, so
/// "no parallel registry" cannot be satisfied by there being no registration at all.
/// </para>
/// <para>
/// Rule 2 - no file under <c>Plugins/</c> may construct a transport or an <c>McpClient</c> directly.
/// Those are the two ingredients of a parallel registry; owning either means owning a second
/// lifecycle.
/// </para>
/// <para>
/// Rule 3 - <c>PluginTrustMode</c> must declare exactly the members <c>SkillTrustMode</c> declares.
/// Two trust vocabularies is how the enforced set and the reported set drift apart (#2682).
/// </para>
/// </remarks>
public sealed class PluginMcpRegistrationFenceArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string PluginSeamDirectory =>
        Path.Combine(RepoRoot, "src", "extensions", "BotNexus.Extensions.Mcp", "Plugins");

    /// <summary>Constructs that would constitute a second MCP lifecycle.</summary>
    private static readonly (string Name, Regex Pattern)[] ParallelRegistryPatterns =
    [
        ("direct McpClient construction", new Regex(@"new\s+McpClient\s*\(", RegexOptions.Compiled)),
        ("direct stdio transport construction", new Regex(@"new\s+StdioMcpTransport\s*\(", RegexOptions.Compiled)),
        ("direct HTTP/SSE transport construction", new Regex(@"new\s+HttpSseMcpTransport\s*\(", RegexOptions.Compiled)),
        ("private transport factory", new Regex(@"CreateTransport\s*\(", RegexOptions.Compiled)),
    ];

    [Fact]
    public void Rule1_PluginSeam_DelegatesToTheExistingServerManager()
    {
        Directory.Exists(PluginSeamDirectory).ShouldBeTrue(
            $"the plugin MCP registration seam is expected at {PluginSeamDirectory}");

        var files = EnumerateSeamFiles().ToList();
        files.ShouldNotBeEmpty("the fence is vacuous if it scans no files");

        var referencesManager = files
            .Select(File.ReadAllText)
            .Any(code => code.Contains("McpServerManager", StringComparison.Ordinal));

        referencesManager.ShouldBeTrue(
            "#2686 requires plugin-declared servers to be registered through the existing " +
            "McpServerManager. No reference to it means either a parallel registry, or no " +
            "registration at all.");
    }

    [Fact]
    public void Rule2_PluginSeam_DoesNotBuildItsOwnClientsOrTransports()
    {
        var violations = (
            from file in EnumerateSeamFiles()
            let code = File.ReadAllText(file)
            from pattern in ParallelRegistryPatterns
            where pattern.Pattern.IsMatch(code)
            select $"{Path.GetRelativePath(RepoRoot, file)}: {pattern.Name}").ToList();

        violations.ShouldBeEmpty(
            "The plugin registration seam must not own MCP connection lifecycle. Starting a client " +
            "or a transport directly is a parallel registry with a second teardown path, which is " +
            "how a plugin's server survives the plugin's removal. Found: " +
            string.Join("; ", violations));
    }

    [Fact]
    public void Rule3_PluginTrustVocabulary_MatchesTheSkillsTrustVocabulary()
    {
        var pluginMembers = EnumMembers(
            Path.Combine(PluginSeamDirectory, "PluginTrust.cs"),
            "PluginTrustMode");

        var skillMembers = EnumMembers(
            Path.Combine(RepoRoot, "src", "extensions", "BotNexus.Extensions.Skills", "Security", "SkillTrustVerifier.cs"),
            "SkillTrustMode");

        skillMembers.ShouldNotBeEmpty("the fence is vacuous if the skills enum could not be parsed");

        pluginMembers.ShouldBe(
            skillMembers,
            ignoreOrder: false,
            "Plugins reuse the skills trust model (#2682). A trust mode present in one vocabulary " +
            $"and absent from the other means a posture that is reportable but not enforceable, or " +
            $"vice versa. Plugin: [{string.Join(", ", pluginMembers)}] Skills: [{string.Join(", ", skillMembers)}]");
    }

    private static string[] EnumMembers(string file, string enumName)
    {
        File.Exists(file).ShouldBeTrue($"expected {file} to exist");
        var code = File.ReadAllText(file);

        var match = Regex.Match(
            code,
            @"enum\s+" + Regex.Escape(enumName) + @"\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);

        match.Success.ShouldBeTrue($"could not locate 'enum {enumName}' in {file}");

        // Strip comments before splitting so a doc comment mentioning a mode is not read as one.
        var body = Regex.Replace(match.Groups["body"].Value, @"//.*?$", string.Empty, RegexOptions.Multiline);
        body = Regex.Replace(body, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        return body
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.Split('=')[0].Trim())
            .Where(m => m.Length > 0 && !m.StartsWith('['))
            .ToArray();
    }

    private static IEnumerable<string> EnumerateSeamFiles()
        => Directory.Exists(PluginSeamDirectory)
            ? Directory.EnumerateFiles(PluginSeamDirectory, "*.cs", SearchOption.AllDirectories)
            : [];

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
