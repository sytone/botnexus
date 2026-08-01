using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Tests.Architecture;

/// <summary>
/// Fences the process-wide <c>BotNexus__ConfigPath</c> environment mutation performed by the
/// integration tests. Any test class that boots the gateway host (and therefore reads
/// configuration derived from that variable) must join the serialising
/// <c>IntegrationTests</c> collection, otherwise xUnit runs it in parallel with the mutators
/// and it observes another class's environment (issue #2665).
/// </summary>
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

        if (!BootsGatewayHost(source))
            yield break;

        if (source.Contains($"[Collection(\"{SerialisingCollection}\")]", StringComparison.Ordinal))
            yield break;

        yield return $"{Path.GetRelativePath(testProjectRoot, filePath)}: boots the gateway host but is not in the \"{SerialisingCollection}\" collection.";
    }

    private static bool IsFenceItself(string filePath)
        => string.Equals(Path.GetFileName(filePath), $"{nameof(ConfigPathTestIsolationTests)}.cs", StringComparison.Ordinal);

    private static bool BootsGatewayHost(string source)
        => Regex.IsMatch(source, @"WebApplicationFactory<\s*Program\s*>", RegexOptions.None, TimeSpan.FromSeconds(5)) ||
           source.Contains("BotNexus__ConfigPath", StringComparison.Ordinal);

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
