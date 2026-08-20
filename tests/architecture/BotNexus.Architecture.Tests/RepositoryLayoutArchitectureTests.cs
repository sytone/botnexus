using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Prevents architecture tests from recreating repository discovery instead of using their
/// instance-scoped <see cref="ArchitectureTest.Repository"/> context.
/// </summary>
public sealed class RepositoryLayoutArchitectureTests : ArchitectureTest
{
    [Fact]
    public void ArchitectureTests_DoNotImplementRepositoryRootDiscovery()
    {
        var projectRoot = Repository.Path("tests", "architecture", "BotNexus.Architecture.Tests");
        var violations = Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !InfrastructureFiles.Contains(Path.GetFileName(file)))
            .Where(file => RepositoryLocatorPattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(Repository.Root, file))
            .ToArray();

        violations.ShouldBeEmpty(
            "Repository root discovery belongs to RepositoryLayout; architecture tests should " +
            "derive from ArchitectureTest and use its Repository instance. Violations: " +
            string.Join(", ", violations));
    }

    private static readonly HashSet<string> InfrastructureFiles =
        new(StringComparer.Ordinal) { "ArchitectureTest.cs", "RepositoryLayoutArchitectureTests.cs" };

    private static readonly Regex RepositoryLocatorPattern = new(
        @"^\s*(?:private|internal|public|protected)\s+(?:static\s+)?string\s+" +
        @"(?:Find(?:Repo|Source|Src|SweepRepo)Root|SrcDir|RepoRoot)\s*(?:\(|=>|\{)" +
        @"|new\s+DirectoryInfo\s*\(\s*AppContext\.BaseDirectory\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
}