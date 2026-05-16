using System.Xml.Linq;

namespace BotNexus.Gateway.Tests.Architecture;

public sealed class GatewayProjectDependencyBoundaryTests
{
    [Fact]
    public void GatewayProjects_DoNotReferenceExtensionsProjectsOrLibraries()
    {
        var repositoryRoot = FindRepositoryRoot();
        var gatewayRoot = Path.Combine(repositoryRoot, "src", "gateway");

        var violations = Directory
            .EnumerateFiles(gatewayRoot, "*.csproj", SearchOption.AllDirectories)
            .SelectMany(projectPath => FindViolations(repositoryRoot, projectPath))
            .ToList();

        violations.ShouldBeEmpty($"""
            Gateway projects must not reference extension projects or libraries.
            Violations:
            {string.Join(Environment.NewLine, violations)}
            """);
    }

    private static IEnumerable<string> FindViolations(string repositoryRoot, string projectPath)
    {
        var project = XDocument.Load(projectPath);
        var relativeProjectPath = Path.GetRelativePath(repositoryRoot, projectPath);

        foreach (var projectReference in project.Descendants().Where(x => x.Name.LocalName == "ProjectReference"))
        {
            var include = projectReference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
                continue;

            if (IsExtensionProjectReference(include))
                yield return $"{relativeProjectPath}: ProjectReference -> {include}";
        }

        foreach (var packageReference in project.Descendants().Where(x => x.Name.LocalName == "PackageReference"))
        {
            var include = packageReference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
                continue;

            if (IsExtensionLibraryReference(include))
                yield return $"{relativeProjectPath}: PackageReference -> {include}";
        }

        foreach (var assemblyReference in project.Descendants().Where(x => x.Name.LocalName == "Reference"))
        {
            var include = assemblyReference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
                continue;

            if (IsExtensionLibraryReference(include))
                yield return $"{relativeProjectPath}: Reference -> {include}";
        }
    }

    private static bool IsExtensionProjectReference(string include)
    {
        var normalizedInclude = include.Replace('/', '\\');
        var fileName = Path.GetFileNameWithoutExtension(normalizedInclude);

        return normalizedInclude.Contains("\\extensions\\", StringComparison.OrdinalIgnoreCase) ||
               IsExtensionLibraryReference(fileName);
    }

    private static bool IsExtensionLibraryReference(string include)
        => include.StartsWith("BotNexus.Extensions.", StringComparison.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
