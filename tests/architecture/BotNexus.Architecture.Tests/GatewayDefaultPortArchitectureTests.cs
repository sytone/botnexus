using System.Text.RegularExpressions;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for issue #2929: the gateway's default listen URL has exactly one
/// spelling under <c>src/</c>.
/// </summary>
/// <remarks>
/// <para>
/// The defect is not that a literal was wrong - it is that there were several literals, so one of them
/// could be wrong without anything noticing. The default moved to 5005 and was centralised, but the
/// startup banner in <c>Program.cs</c> kept a pre-centralisation <c>http://localhost:5000</c>. Being a
/// fallback, it was silent whenever a listen URL was configured, which it normally is; a fresh install
/// was told the gateway was on a port nothing was listening on. Replacing that one literal fixes today.
/// This fence is what stops the next copy.
/// </para>
/// <para>
/// The fence scans <c>src/**/*.cs</c> for a gateway-shaped default URL - a loopback or any-address host
/// carrying the gateway port, or the stale 5000 spelling of it - and permits it only in
/// <c>GatewayDefaults.cs</c>. Comments, XML doc and the wildcard illustrations are excluded
/// deliberately: <c>PlatformConfig</c> and <c>PlatformConfigValidator</c> cite <c>http://+:5000</c> as a
/// Kestrel wildcard example, which is a legitimate mention of a binding form, not a second default.
/// Likewise the shipped <c>Dockerfile</c> binds <c>http://+:5000</c> explicitly - an explicit
/// <c>ASPNETCORE_URLS</c>, never a fallback - and is out of scope here.
/// </para>
/// </remarks>
public sealed class GatewayDefaultPortArchitectureTests
{
    /// <summary>The single file permitted to spell the gateway's default listen URL.</summary>
    private const string CanonicalDefinition = "GatewayDefaults.cs";

    /// <summary>
    /// Matches a gateway-shaped default listen URL: an explicit loopback or any-address host bound to
    /// the canonical gateway port, or to the stale 5000 this issue removed. Restricting the host set
    /// keeps unrelated local services (Ollama on 11434, vLLM on 8000, OTLP on 4317) out of scope.
    /// </summary>
    private static readonly Regex GatewayDefaultUrlShape = new(
        @"(?:localhost|127\.0\.0\.1|0\.0\.0\.0)\s*:\s*500[05]\b",
        RegexOptions.Compiled);

    [Fact]
    public void GatewayDefaultListenUrl_IsSpelledInExactlyOnePlaceUnderSrc()
    {
        var repoRoot = FindRepoRoot();
        var sourceRoot = Path.Combine(repoRoot, "src");
        Directory.Exists(sourceRoot).ShouldBeTrue($"Expected a src directory at '{sourceRoot}'.");

        var candidates = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        // Non-vacuity: an empty or near-empty candidate set would pass for the wrong reason.
        candidates.Count.ShouldBeGreaterThan(100,
            "The fence found almost no source files, so a pass would prove nothing. Check the repo-root resolution.");

        var offenders = new List<string>();
        var canonicalSeen = false;

        foreach (var path in candidates)
        {
            var code = StripComments(File.ReadAllText(path));
            if (!GatewayDefaultUrlShape.IsMatch(code))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(path), CanonicalDefinition, StringComparison.Ordinal))
            {
                canonicalSeen = true;
                continue;
            }

            offenders.Add(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'));
        }

        // Non-vacuity: the fence must actually be matching the canonical definition. If GatewayDefaults
        // were renamed or its literal reshaped, an empty offender list would mean nothing.
        canonicalSeen.ShouldBeTrue(
            $"The canonical definition '{CanonicalDefinition}' was not matched by this fence's own pattern. "
            + "Either it moved or the literal's shape changed - update this fence deliberately.");

        offenders.ShouldBeEmpty(
            "A second spelling of the gateway's default listen URL exists under src/. That is exactly how "
            + "issue #2929 happened: the default moved to 5005 and one copy kept announcing 5000, silently, "
            + "because it was only ever a fallback. Reference BotNexus.Gateway.Configuration.GatewayDefaults "
            + "instead of writing another literal.\nOffenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The stale port must not reappear anywhere under <c>src/</c> as a default URL - including in the
    /// canonical file. This is the clause that reddens if <c>Program.cs</c>'s literal is reverted.
    /// </summary>
    [Fact]
    public void StaleGatewayPort5000_DoesNotAppearAsADefaultUrlUnderSrc()
    {
        var repoRoot = FindRepoRoot();
        var stale = new Regex(@"(?:localhost|127\.0\.0\.1|0\.0\.0\.0)\s*:\s*5000\b", RegexOptions.Compiled);

        var offenders = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => stale.IsMatch(StripComments(File.ReadAllText(path))))
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .ToList();

        offenders.ShouldBeEmpty(
            $"The gateway binds port {GatewayDefaults.ListenPort}; a source default naming 5000 sends an "
            + "operator to a port nothing is listening on (issue #2929). Kestrel wildcard illustrations "
            + "(http://+:5000) are unaffected - this clause matches explicit loopback/any-address hosts only.\n"
            + "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Removes line and block comments so that prose describing the defect - including this fence's own
    /// citation of it - does not trip the scan. String literals are deliberately KEPT: a hardcoded
    /// default lives in a string, so stripping them would make the fence structurally incapable of
    /// firing, the failure mode #2700 exists to prevent.
    /// </summary>
    /// <remarks>
    /// The line-comment pattern requires that the <c>//</c> is not preceded by <c>:</c>. Without that
    /// guard the scheme separator in <c>http://localhost:5005</c> is itself read as the start of a
    /// comment, the rest of the line is deleted, and the fence silently matches nothing at all - it
    /// passed vacuously in exactly that way while being written.
    /// </remarks>
    private static string StripComments(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"(?<!:)//[^\r\n]*", " ");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the repository root from " + AppContext.BaseDirectory);
    }
}
