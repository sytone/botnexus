using System.Text.RegularExpressions;
using BotNexus.Gateway.Tests.Diagnostics;

namespace BotNexus.Gateway.Tests.Architecture;

/// <summary>
/// Fences the ambient static <c>ProviderDiagnostics.LoggerFactory</c> against the same hazard
/// <see cref="ConfigPathTestIsolationTests"/> fences for <c>BotNexus__ConfigPath</c>: a test class
/// that assigns the shared static must join a <c>DisableParallelization</c> collection, otherwise
/// xUnit runs it concurrently with the other mutators and one restores the factory mid-flight
/// through another's assertion (issue #3018).
/// </summary>
/// <remarks>
/// The hazard was already diagnosed and fixed once, in <c>BotNexus.Agent.Providers.Core.Tests</c>.
/// It did not propagate here because a <c>[CollectionDefinition]</c> is resolved per test ASSEMBLY,
/// so the existing definition could not serialise these classes no matter how correct it was. That
/// silent, invisible limit is exactly what this fence exists to make loud: enrolment is now checked
/// mechanically inside THIS assembly, so a third mutating class cannot be added unenrolled.
/// </remarks>
public sealed class ProviderDiagnosticsIsolationTests
{
    [Fact]
    public void TestClasses_MutatingProviderDiagnosticsLoggerFactory_JoinTheSerialisingCollection()
    {
        var testProjectRoot = FindTestProjectRoot();

        var violations = EnumerateSourceFiles(testProjectRoot)
            .Where(path => !IsFenceItself(path))
            .Where(path => MutatesProviderDiagnostics(StripComments(File.ReadAllText(path))))
            .Where(path => !IsEnrolled(StripComments(File.ReadAllText(path))))
            .Select(path => Path.GetRelativePath(testProjectRoot, path))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty($"""
            Test classes that assign the ambient static ProviderDiagnostics.LoggerFactory must be
            annotated with [Collection(ProviderDiagnosticsCollection.Name)] so xUnit serialises them
            against each other (issue #3018). A [CollectionDefinition] does not cross assembly
            boundaries, so the definition in BotNexus.Agent.Providers.Core.Tests cannot cover these;
            use the one in BotNexus.Gateway.Tests/Diagnostics/ProviderDiagnosticsCollection.cs.
            Violations:
            {string.Join(Environment.NewLine, violations)}
            """);
    }

    [Fact]
    public void ProviderDiagnosticsMutatingTestClasses_AreDetectedByTheFence()
    {
        // Non-vacuity guard (AC2): a fence whose candidate set is empty passes for the wrong
        // reason. Both known mutators must be seen by the detector itself.
        var testProjectRoot = FindTestProjectRoot();

        var mutators = EnumerateSourceFiles(testProjectRoot)
            .Where(path => !IsFenceItself(path))
            .Where(path => MutatesProviderDiagnostics(StripComments(File.ReadAllText(path))))
            .Select(Path.GetFileName)
            .ToList();

        mutators.ShouldContain("AgentHandleImageDropGuardTests.cs");
        mutators.ShouldContain("TextualAttachmentInliningTests.cs");
        mutators.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void TheSerialisingCollection_DisablesParallelization()
    {
        // The [Collection] attributes are inert unless the definition in THIS assembly actually
        // disables parallelization. Pin the definition itself, not just enrolment in it.
        var definition = typeof(ProviderDiagnosticsCollection)
            .GetCustomAttributes(typeof(CollectionDefinitionAttribute), inherit: false)
            .Cast<CollectionDefinitionAttribute>()
            .SingleOrDefault();

        definition.ShouldNotBeNull();
        definition.DisableParallelization.ShouldBeTrue();
        typeof(ProviderDiagnosticsCollection).Assembly.ShouldBe(typeof(ProviderDiagnosticsIsolationTests).Assembly);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string testProjectRoot)
        => Directory
            .EnumerateFiles(testProjectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the source ASSIGNS the shared static. Merely reading it is harmless, so the
    /// pattern requires an assignment target -- a class that only saves the previous value is not
    /// the hazard, and over-reporting would train readers to suppress the fence.
    /// </summary>
    private static bool MutatesProviderDiagnostics(string code)
        => Regex.IsMatch(
            code,
            @"ProviderDiagnostics\s*\.\s*LoggerFactory\s*=",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

    private static bool IsEnrolled(string code)
        => Regex.IsMatch(
               code,
               @"\[\s*Collection\s*\(\s*(?:[\w.]*\.)?ProviderDiagnosticsCollection\s*\.\s*Name\s*\)\s*\]",
               RegexOptions.None,
               TimeSpan.FromSeconds(5)) ||
           code.Contains($"[Collection(\"{ProviderDiagnosticsCollection.Name}\")]", StringComparison.Ordinal);

    /// <summary>
    /// Removes line and block comments so the fence matches real code rather than documentation --
    /// this very file, and the collection definition's own doc comment, name the static in prose.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        return Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5));
    }

    private static bool IsFenceItself(string filePath)
        => string.Equals(Path.GetFileName(filePath), $"{nameof(ProviderDiagnosticsIsolationTests)}.cs", StringComparison.Ordinal);

    private static string FindTestProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                return Path.Combine(current.FullName, "tests", "gateway", "BotNexus.Gateway.Tests");

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
