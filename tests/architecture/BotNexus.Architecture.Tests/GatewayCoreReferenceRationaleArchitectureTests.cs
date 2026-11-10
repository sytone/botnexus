using System.Xml.Linq;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Keeps the temporary #3040 reference debt attached to its actual project edge, without granting
/// an implementation-facing exemption or requiring that edge to survive the #3251 child migrations.
/// The shrink-only equality IL fence remains the authority for public signature leakage.
/// </summary>
public sealed class GatewayCoreReferenceRationaleArchitectureTests : ArchitectureTest
{
    private const string CoreReference = "../../agent/BotNexus.Agent.Core/BotNexus.Agent.Core.csproj";
    private const string DomainReference = "../../domain/BotNexus.Domain/BotNexus.Domain.csproj";

    /// <summary>Each remaining direct core edge must explain its debt and migration owners locally.</summary>
    [Theory]
    [InlineData("Contracts")]
    [InlineData("Abstractions")]
    public void GatewayProject_CoreReference_HasAdjacentResidualRationale(string project)
    {
        var name = $"BotNexus.Gateway.{project}";
        var path = Repository.Path("src", "gateway", name, $"{name}.csproj");
        FindViolations(project, XDocument.Load(path)).ShouldBeEmpty(
            "#3040 AC6: document each residual Agent.Core reference immediately before that edge; " +
            "this is temporary debt, not permission to expose implementation types.");
    }

    /// <summary>A correctly attached comment is accepted with either MSBuild path separator.</summary>
    [Theory]
    [InlineData("Contracts")]
    [InlineData("Abstractions")]
    public void RationaleInspector_AdjacentComment_AcceptsBothPathSeparators(string project)
    {
        foreach (var reference in new[] { CoreReference, CoreReference.Replace('/', '\\') })
            FindViolations(project, Fixture(project, reference)).ShouldBeEmpty();
    }

    /// <summary>Removing the explanation while retaining the edge is detected.</summary>
    [Theory]
    [InlineData("Contracts")]
    [InlineData("Abstractions")]
    public void RationaleInspector_AbsentComment_RejectsCoreReference(string project)
    {
        var xml = Fixture(project);
        xml.DescendantNodes().OfType<XComment>().Single().Remove();
        FindViolations(project, xml).ShouldHaveSingleItem();
    }

    /// <summary>A comment elsewhere in the project, after the edge, or above its group is not local rationale.</summary>
    [Theory]
    [InlineData("Contracts")]
    [InlineData("Abstractions")]
    public void RationaleInspector_MisplacedComment_RejectsCoreReference(string project)
    {
        foreach (var placement in new[] { "after", "outside-group", "intervening-comment" })
        {
            var xml = Fixture(project);
            var comment = xml.DescendantNodes().OfType<XComment>().Single();
            var core = xml.Descendants("ProjectReference").First();
            comment.Remove();
            if (placement == "after")
                core.AddAfterSelf(comment);
            else if (placement == "outside-group")
                xml.Root?.AddFirst(comment);
            else
                core.AddBeforeSelf(comment, new XComment("Unrelated explanation"));
            FindViolations(project, xml).ShouldHaveSingleItem($"Misplacement: {placement}");
        }
    }

    /// <summary>Attaching the right words to another reference must not excuse the core edge.</summary>
    [Theory]
    [InlineData("Contracts")]
    [InlineData("Abstractions")]
    public void RationaleInspector_CommentOnWrongReference_RejectsCoreReference(string project)
    {
        var xml = Fixture(project);
        var comment = xml.DescendantNodes().OfType<XComment>().Single();
        comment.Remove();
        xml.Descendants("ProjectReference").Last().AddBeforeSelf(comment);
        FindViolations(project, xml).ShouldHaveSingleItem();
    }

    /// <summary>Generic debt wording cannot replace the concrete residual reasons or issue owners.</summary>
    [Theory]
    [InlineData("Contracts")]
    [InlineData("Abstractions")]
    public void RationaleInspector_MissingReasonOrOwner_RejectsCoreReference(string project)
    {
        foreach (var required in RequiredTerms(project))
        {
            var xml = Fixture(project);
            var comment = xml.DescendantNodes().OfType<XComment>().Single();
            comment.Value = comment.Value.Replace(required, "omitted", StringComparison.Ordinal);
            FindViolations(project, xml).ShouldHaveSingleItem($"Missing term: {required}");
        }
    }

    /// <summary>Once the core edge is removed, neither the edge nor its obsolete rationale is required.</summary>
    [Theory]
    [InlineData("Contracts")]
    [InlineData("Abstractions")]
    public void RationaleInspector_RemovedCoreReference_AllowsMigration(string project)
    {
        var xml = Fixture(project);
        xml.Descendants("ProjectReference").First().Remove();
        xml.DescendantNodes().OfType<XComment>().Single().Remove();
        FindViolations(project, xml).ShouldBeEmpty();
    }

    private static string[] RequiredTerms(string project) => project switch
    {
        "Contracts" => ["Temporary", "not an implementation-facing exemption", "#3040", "#3251",
            "IAgentTool", "#3446", "IAgentHandleInspector.ResolveTool", "IAgentToolFactory.CreateTools",
            "CommandExecutionContext.ResolveSessionTool"],
        "Abstractions" => ["Temporary", "not an implementation-facing exemption", "#3040", "#3251",
            "AgentToolContribution.Tools", "IAgentTool", "#3446"],
        _ => throw new ArgumentOutOfRangeException(nameof(project), project, "Unknown fenced project")
    };

    private static XDocument Fixture(string project, string reference = CoreReference) => new(
        new XElement("Project", new XElement("ItemGroup",
            new XComment(string.Join("; ", RequiredTerms(project))),
            new XElement("ProjectReference", new XAttribute("Include", reference)),
            new XElement("ProjectReference", new XAttribute("Include", DomainReference)))));

    private static IReadOnlyList<string> FindViolations(string project, XDocument xml)
    {
        var violations = new List<string>();
        foreach (var reference in xml.Descendants().Where(element =>
                     element.Name.LocalName == "ProjectReference" &&
                     ((string?)element.Attribute("Include"))?.Replace('\\', '/') == CoreReference))
        {
            // Ignore formatting whitespace only: an intervening element or unrelated comment breaks
            // the association, even when the required words occur elsewhere in the project file.
            var previous = reference.PreviousNode;
            while (previous is XText text && string.IsNullOrWhiteSpace(text.Value))
                previous = previous.PreviousNode;
            if (previous is not XComment comment || RequiredTerms(project).Any(term =>
                    !comment.Value.Contains(term, StringComparison.Ordinal)))
                violations.Add($"{project}: Agent.Core needs an immediately preceding XML comment " +
                    "with the temporary residual reasons and owning migration issues.");
        }
        return violations;
    }
}
