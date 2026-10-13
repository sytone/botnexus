using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BotNexus.Architecture.Tests;

public sealed class LocalChildEnvironmentArchitectureTests : ArchitectureTest
{
    [Theory]
    [InlineData("src/gateway/BotNexus.Tools/ShellTool.cs", 3)]
    [InlineData("src/extensions/BotNexus.Extensions.ExecTool/ExecTool.cs", 1)]
    public void EveryTargetedStartInfo_UsesUnconditionalBuilderBeforeStart(string path, int expectedSites)
    {
        var source = File.ReadAllText(Path.Combine(Repository.Root, path));
        var (sites, violations) = Inspect(source);
        sites.ShouldBe(expectedSites);
        violations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("var psi = new ProcessStartInfo(); Process.Start(psi);", false)]
    [InlineData("var psi = new ProcessStartInfo(); ProcessEnvironment.Merge(psi.Environment, env); Process.Start(psi);", false)]
    [InlineData("var psi = new ProcessStartInfo(); // LocalChildEnvironment.Apply(psi);\n Process.Start(psi);", false)]
    [InlineData("var psi = new ProcessStartInfo(); if (enabled) LocalChildEnvironment.Apply(psi); Process.Start(psi);", false)]
    [InlineData("var psi = new ProcessStartInfo(); LocalChildEnvironment.Apply(other); Process.Start(psi);", false)]
    [InlineData("var psi = new ProcessStartInfo(); Process.Start(psi); LocalChildEnvironment.Apply(psi);", false)]
    [InlineData("Process.Start(new ProcessStartInfo());", false)]
    [InlineData("var psi = new ProcessStartInfo(); LocalChildEnvironment.Apply(psi); psi.Environment[\"TOKEN\"] = secret; Process.Start(psi);", false)]
    [InlineData("var psi = new ProcessStartInfo(); LocalChildEnvironment.Apply(psi); if (enabled) psi = new(); Process.Start(psi);", false)]
    [InlineData("var psi = new ProcessStartInfo(); LocalChildEnvironment.Apply(psi); Process.Start(other);", false)]
    [InlineData("var psi = new ProcessStartInfo(); LocalChildEnvironment.Apply(psi); Action a = () => Process.Start(psi);", false)]
    [InlineData("var psi = new ProcessStartInfo(); LocalChildEnvironment.Apply(psi); var p = new Process { StartInfo = other }; p.Start();", false)]
    [InlineData("var psi = new ProcessStartInfo(); LocalChildEnvironment.Apply(psi); var p = new Process { StartInfo = psi }; p.Start();", true)]
    [InlineData("var psi = new ProcessStartInfo(); LocalChildEnvironment.Apply(psi); Process.Start(psi);", true)]
    public void Inspector_RejectsSyntheticBypasses(string body, bool valid)
    {
        var (sites, violations) = Inspect("class C { void M() { " + body + " } }");
        sites.ShouldBe(1);
        (violations.Count == 0).ShouldBe(valid);
    }

    private static (int Sites, List<string> Violations) Inspect(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var creations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => creation.Type.ToString() == "ProcessStartInfo").ToArray();
        var sites = creations.Length;
        var violations = new List<string>();
        foreach (var creation in creations)
        {
            if (creation.Parent is not EqualsValueClauseSyntax initializer
                || initializer.Parent is not VariableDeclaratorSyntax variable)
            {
                violations.Add("Untracked ProcessStartInfo construction");
                continue;
            }
            var method = variable.Ancestors().OfType<MethodDeclarationSyntax>().First();
            var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
            var apply = calls.FirstOrDefault(call => call.Expression.ToString() == "LocalChildEnvironment.Apply"
                && call.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() == variable.Identifier.ValueText
                && call.SpanStart > variable.SpanStart
                && call.Parent is ExpressionStatementSyntax statement && statement.Parent == variable.Ancestors().OfType<BlockSyntax>().First());
            var block = variable.Ancestors().OfType<BlockSyntax>().First();
            var processNames = block.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(candidate => candidate.Initializer?.Value is ObjectCreationExpressionSyntax processCreation
                    && processCreation.Type.ToString() == "Process"
                    && processCreation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                        .Any(assignment => assignment.Left.ToString() == "StartInfo"
                            && assignment.Right.ToString() == variable.Identifier.ValueText) == true)
                .Select(candidate => candidate.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
            var start = calls.FirstOrDefault(call =>
                call.Ancestors().Contains(block)
                && !call.Ancestors().Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                && ((call.Expression.ToString() == "Process.Start"
                        && call.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() == variable.Identifier.ValueText)
                    || (call.Expression is MemberAccessExpressionSyntax member
                        && member.Name.Identifier.ValueText == "Start"
                        && processNames.Contains(member.Expression.ToString()))));
            var writes = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(write => write.Left.ToString() == variable.Identifier.ValueText
                    || write.Left.ToString().StartsWith(variable.Identifier.ValueText + ".Environment", StringComparison.Ordinal));
            var mutations = calls.Any(call => call.Expression.ToString().StartsWith(
                variable.Identifier.ValueText + ".Environment.", StringComparison.Ordinal));
            if (apply is null || start is null || apply.SpanStart >= start.SpanStart || writes || mutations)
                violations.Add(method.Identifier.ValueText + ":" + variable.Identifier.ValueText);
        }
        return (sites, violations);
    }
}
