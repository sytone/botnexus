using System.Xml.Linq;

namespace BotNexus.Architecture.Tests;

/// <summary>Protects agent-layer independence, retaining only the existing secret-redaction leaf edge.</summary>
public sealed class AgentProjectBoundaryArchitectureTests : ArchitectureTest
{
    [Fact]
    public void AgentProjects_ProjectReferences_StayWithinAgentLayerExceptExistingWireSeam()
    {
        var root = Repository.Path("src", "agent");
        var projects = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "obj" or "bin"))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        projects.ShouldNotBeEmpty();
        projects.SelectMany(project => BuildInputs(project)
            .SelectMany(input => FindViolations(Repository.Root, project, File.ReadAllText(input))))
            .ShouldBeEmpty("agent projects may not take host-layer dependencies");
    }

    [Theory]
    [InlineData("../BotNexus.Agent.Core/BotNexus.Agent.Core.csproj", true)]
    [InlineData("..\\BotNexus.Agent.Core\\BotNexus.Agent.Core.csproj", true)]
    [InlineData("../../domain/BotNexus.Domain.Wire/BotNexus.Domain.Wire.csproj", true)]
    [InlineData("../../domain/BotNexus.Domain/BotNexus.Domain.csproj", false)]
    [InlineData("../../gateway/BotNexus.Gateway/BotNexus.Gateway.csproj", false)]
    [InlineData("../../agent-other/External.csproj", false)]
    [InlineData("../%2e%2e/gateway/External.csproj", false)]
    [InlineData("$(HostProject)", false)]
    public void BoundaryInspector_NormalizedReferences_AllowOnlyAgentProjectsAndExactWireEdge(string reference, bool allowed)
    {
        var project = Repository.Path("src", "agent", "BotNexus.Agent.Providers.Core", "BotNexus.Agent.Providers.Core.csproj");
        var xml = new XDocument(new XElement("Project", new XElement("ItemGroup",
            new XElement("ProjectReference", new XAttribute("Include", reference))))).ToString();
        FindViolations(Repository.Root, project, xml).Any().ShouldBe(!allowed);
    }

    [Fact]
    public void BoundaryInspector_ConditionalXmlNamespacedReference_IsNotIgnored()
    {
        var project = Repository.Path("src", "agent", "BotNexus.Agent.Core", "BotNexus.Agent.Core.csproj");
        const string xml = """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <!-- <ProjectReference Include="not-real.csproj" /> -->
              <ItemGroup Condition="'$(Configuration)' == 'Release'">
                <ProjectReference Include="../../domain/BotNexus.Domain.Wire/BotNexus.Domain.Wire.csproj" />
              </ItemGroup>
            </Project>
            """;
        FindViolations(Repository.Root, project, xml).ShouldHaveSingleItem();
    }

    [Fact]
    public void BoundaryInspector_RemovalsAndMetadataUpdates_DoNotAddDependencies()
    {
        var project = Repository.Path("src", "agent", "BotNexus.Agent.Core", "BotNexus.Agent.Core.csproj");
        const string xml = """
            <Project><ItemGroup>
              <ProjectReference Remove="@(ProjectReference)" />
              <ProjectReference Update="../BotNexus.Agent.Providers.Core/BotNexus.Agent.Providers.Core.csproj" Private="false" />
            </ItemGroup></Project>
            """;
        FindViolations(Repository.Root, project, xml).ShouldBeEmpty();
    }

    [Fact]
    public void BoundaryInspector_ExplicitImports_FailClosedUntilTheirGraphIsInspected()
    {
        var project = Repository.Path("src", "agent", "BotNexus.Agent.Core", "BotNexus.Agent.Core.csproj");
        FindViolations(Repository.Root, project, "<Project><Import Project=\"shared.props\" /></Project>")
            .ShouldHaveSingleItem();
    }

    private IEnumerable<string> BuildInputs(string project)
    {
        yield return project;
        var directory = new DirectoryInfo(Path.GetDirectoryName(project)
            ?? throw new InvalidOperationException($"Project has no directory: {project}"));
        while (directory is not null)
        {
            foreach (var filename in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                var input = Path.Combine(directory.FullName, filename);
                if (File.Exists(input))
                    yield return input;
            }
            if (directory.FullName == Repository.Root)
                break;
            directory = directory.Parent;
        }
    }

    private static IReadOnlyList<string> FindViolations(string repositoryRoot, string project, string xml)
    {
        var violations = new List<string>();
        var projectDirectory = Path.GetDirectoryName(project)
            ?? throw new InvalidOperationException($"Project has no directory: {project}");
        var agentRoot = Path.Combine(repositoryRoot, "src", "agent");
        var source = Path.GetRelativePath(repositoryRoot, project).Replace('\\', '/');
        var document = XDocument.Parse(xml);
        if (document.Descendants().Any(element => element.Name.LocalName is "Import" or "ImportGroup"))
            violations.Add($"{source}: explicit imports require extending the inspected build-input graph");
        foreach (var reference in document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference"))
        {
            var include = reference.Attribute("Include")?.Value;
            if (include is null && (reference.Attribute("Remove") is not null || reference.Attribute("Update") is not null))
                continue;
            if (string.IsNullOrWhiteSpace(include) || include.IndexOfAny(['$', '@', '*', '?', ';', '%']) >= 0)
            {
                violations.Add($"{source}: reference must be a literal, independently inspectable project path");
                continue;
            }
            var target = Path.GetFullPath(Path.Combine(projectDirectory,
                include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(agentRoot, target);
            var inAgentLayer = !Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            var targetPath = Path.GetRelativePath(repositoryRoot, target).Replace('\\', '/');
            var existingWireSeam = source == "src/agent/BotNexus.Agent.Providers.Core/BotNexus.Agent.Providers.Core.csproj"
                && targetPath == "src/domain/BotNexus.Domain.Wire/BotNexus.Domain.Wire.csproj";
            if (!inAgentLayer && !existingWireSeam)
                violations.Add($"{source}: prohibited project reference to {targetPath}");
        }
        return violations;
    }
}