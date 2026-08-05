using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Tests.Architecture;

/// <summary>
/// Fences the process-wide <c>BotNexus__ConfigPath</c> environment mutation performed by the
/// integration tests. Any test class that boots the gateway host (and therefore reads
/// configuration derived from that variable) must join the serialising
/// <c>IntegrationTests</c> collection, otherwise xUnit runs it in parallel with the mutators
/// and it observes another class's environment (issue #2665).
/// </summary>
/// <remarks>
/// #2825 widened what counts as "reads configuration derived from that variable". The original
/// fence recognised only classes booting a <c>WebApplicationFactory&lt;Program&gt;</c> or naming
/// <c>BotNexus__ConfigPath</c> directly. A class that merely builds its own configuration root with
/// <c>AddJsonFile(reloadOnChange: true)</c> and awaits a reload callback is just as exposed, because
/// that pipeline is process-global. Two such classes carried no <c>[Collection]</c> attribute at
/// all -- and with <c>parallelizeTestCollections: true</c> an unattributed class is its OWN
/// collection, running concurrently with the mutators rather than after them.
///
/// <para>
/// The race was invisible on a fast Windows dev box, where the reload callback lands in roughly
/// 270ms and the interleaving rarely occurred, and reproduced consistently under the Linux
/// container gate at four failures per run. A probe inside that same container image proved the
/// reload pipeline is healthy there -- it fires correctly even across the atomic temp-file swap
/// <c>PlatformConfigWriter</c> performs -- which refutes the tempting inotify-versus-inode-swap
/// explanation and leaves unsynchronised global state as the only cause.
/// </para>
/// </remarks>
public sealed class ConfigPathTestIsolationTests
{
    private const string SerialisingCollection = "IntegrationTests";

    [Fact]
    public void TestClasses_ReadingConfigPathDerivedConfiguration_JoinTheSerialisingCollection()
    {
        var testProjectRoot = FindTestProjectRoot();

        var violations = Directory
            .EnumerateFiles(testProjectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => FindViolations(testProjectRoot, path))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty($"""
            Test classes that boot the gateway host read configuration derived from the
            process-wide BotNexus__ConfigPath environment variable, which the integration tests
            mutate. They must be annotated with [Collection("{SerialisingCollection}")] so xUnit
            serialises them against the mutators (issue #2665).
            Violations:
            {string.Join(Environment.NewLine, violations)}
            """);
    }

    [Fact]
    public void ConfigPathMutatingTestClasses_AreDetectedByTheFence()
    {
        // Non-vacuity guard: the fence is worthless if it matches nothing.
        var testProjectRoot = FindTestProjectRoot();

        var hostBootingClasses = Directory
            .EnumerateFiles(testProjectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsFenceItself(path))
            .Count(path => BootsGatewayHost(File.ReadAllText(path)));

        hostBootingClasses.ShouldBeGreaterThan(5);
    }

    private static IEnumerable<string> FindViolations(string testProjectRoot, string filePath)
    {
        if (IsFenceItself(filePath))
            yield break;

        var source = File.ReadAllText(filePath);

        // #2825: match on CODE, not prose. The reload marker is a source substring, so a comment
        // merely MENTIONING `reloadOnChange: true` (PlatformConfigurationTests documents the
        // pipeline in a comment without ever building a watcher) would otherwise be reported as a
        // violation. A fence that cries wolf on documentation gets suppressed rather than obeyed.
        var code = StripComments(source);

        if (!BootsGatewayHost(code))
            yield break;

        if (code.Contains($"[Collection(\"{SerialisingCollection}\")]", StringComparison.Ordinal))
            yield break;

        yield return $"{Path.GetRelativePath(testProjectRoot, filePath)}: boots the gateway host but is not in the \"{SerialisingCollection}\" collection.";
    }

    /// <summary>
    /// Removes line and block comments so the fence matches real code rather than documentation.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        return Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5));
    }

    private static bool IsFenceItself(string filePath)
        => string.Equals(Path.GetFileName(filePath), $"{nameof(ConfigPathTestIsolationTests)}.cs", StringComparison.Ordinal);

    private static bool BootsGatewayHost(string source)
        => Regex.IsMatch(source, @"WebApplicationFactory<\s*Program\s*>", RegexOptions.None, TimeSpan.FromSeconds(5)) ||
           source.Contains("BotNexus__ConfigPath", StringComparison.Ordinal) ||
           // #2825: a class that builds a RELOADING configuration root is exposed to the same
           // process-global state even though it never boots the host. The reload pipeline and the
           // environment variables it derives from are process-wide, so a concurrent class swapping
           // BOTNEXUS_HOME out from under it corrupts the read exactly as a host-booting class would.
           source.Contains("reloadOnChange: true", StringComparison.Ordinal) ||
           source.Contains("HomeOverrideEnvVar", StringComparison.Ordinal);

    private static string FindTestProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return Path.Combine(current.FullName, "tests", "gateway", "BotNexus.Gateway.Tests");

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
