using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BotNexus.Architecture.Tests;

/// <summary>Fences contract organization without treating comments or formatting as API.</summary>
public sealed class AgentExtensionLayoutArchitectureTests : ArchitectureTest
{
    [Fact]
    public void CoreExtensionContracts_KeepResponsibilityFoldersAndMatchingFiles()
    {
        var root = Repository.Path("src", "agent", "BotNexus.Agent.Core");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: Path.GetRelativePath(root, path).Replace('\\', '/'), FullPath: path))
            .Where(file => !file.Path.Split('/').Any(part => part is "obj" or "bin"))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        files.Where(file => file.Path.StartsWith("ExtensionPoints/", StringComparison.Ordinal))
            .ShouldNotBeEmpty("the extension tree must not disappear and make the fence vacuous");
        typeof(BotNexus.Agent.Core.Agent).Assembly.GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith("BotNexus.Agent.Core.ExtensionPoints.", StringComparison.Ordinal) == true)
            .ShouldNotBeEmpty("family files must still expose actual public extension contracts");
        Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.Split('/').Any(part => part is "obj" or "bin"))
            .Where(path => path.Split('/').Any(part => part.Equals("Hooks", StringComparison.OrdinalIgnoreCase)))
            .ShouldBeEmpty("core extension points belong to responsibility families, not Hooks");

        files.SelectMany(file => FindViolations(file.Path, File.ReadAllText(file.FullPath)))
            .ShouldBeEmpty("keep public extension declarations in matching responsibility-family files");
    }

    [Theory]
    [InlineData("public delegate void Policy();")]
    [InlineData("public record Policy;")]
    [InlineData("public record struct Policy;")]
    [InlineData("public interface Policy { }")]
    [InlineData("public enum Policy { Allow }")]
    [InlineData("public struct Policy { }")]
    [InlineData("public class Policy { public class Nested { } }")]
    public void LayoutInspector_PublicDeclarationKinds_AcceptMatchingFiles(string declaration)
    {
        FindViolations("ExtensionPoints/Tools/Policy.cs",
            "namespace BotNexus.Agent.Core.ExtensionPoints.Tools; " + declaration).ShouldBeEmpty();
    }

    [Fact]
    public void LayoutInspector_BlockAndNestedNamespaces_IgnoreCommentsStringsAndNestedTypes()
    {
        const string source = """
            // public delegate void Fake(); namespace BotNexus.Agent.Core.Hooks;
            namespace BotNexus.Agent.Core
            {
                namespace ExtensionPoints.Tools
                {
                    public class Policy
                    {
                        public const string Example = "public class Fake {}";
                        public class Nested { }
                    }
                }
            }
            """;
        FindViolations("ExtensionPoints/Tools/Policy.cs", source).ShouldBeEmpty();
    }

    [Fact]
    public void LayoutInspector_NamespaceTriviaEscapesAndInternalHelpers_DoNotChangeLayout()
    {
        FindViolations("ExtensionPoints/Tools/Policy.cs",
            "namespace @BotNexus /* example */ . Agent.Core.ExtensionPoints.Tools; public record Policy;")
            .ShouldBeEmpty();
        FindViolations("ExtensionPoints/Tools/Helper.cs",
            "namespace BotNexus.Agent.Core.ExtensionPoints.Tools; internal class Helper { }")
            .ShouldBeEmpty();
        FindViolations("Types/Policy.cs",
            "namespace BotNexus.Agent.Core.@Hooks; public record Policy;").ShouldNotBeEmpty();
    }

    [Fact]
    public void LayoutInspector_ConditionalDeclarations_InspectEverySymbolCombination()
    {
        const string source = """
            namespace BotNexus.Agent.Core.Configuration;
            #if NET10_0 && !DEBUG
            public delegate void Policy();
            #endif
            """;
        FindViolations("Configuration/Policy.cs", source).ShouldNotBeEmpty();
        FindViolations("ExtensionPoints/Tools/Policy.cs", source.Replace(
            "BotNexus.Agent.Core.Configuration", "BotNexus.Agent.Core.ExtensionPoints.Tools",
            StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ExtensionPoints/Tools/Wrong.cs", "namespace BotNexus.Agent.Core.ExtensionPoints.Tools; public delegate void Policy();")]
    [InlineData("ExtensionPoints/Tools/Policy.cs", "namespace Wrong; public record Policy;")]
    [InlineData("ExtensionPoints/Policy.cs", "namespace BotNexus.Agent.Core.ExtensionPoints; public record Policy;")]
    [InlineData("ExtensionPoints/Tools/Policy.cs", "namespace BotNexus.Agent.Core.ExtensionPoints.Tools; public record Policy; public enum Decision { Allow }")]
    [InlineData("Configuration/Policy.cs", "namespace BotNexus.Agent.Core.Configuration; public delegate void Policy();")]
    [InlineData("Types/Policy.cs", "namespace BotNexus.Agent.Core.ExtensionPoints.Tools; public record Policy;")]
    [InlineData("Configuration/Delegates.cs", "namespace BotNexus.Agent.Core.Configuration; internal class Utility { }")]
    [InlineData("Hooks/Policy.cs", "namespace BotNexus.Agent.Core.Hooks; public record Policy;")]
    [InlineData("Types/Policy.cs", "namespace BotNexus.Agent.Core.Hooks; public record Policy;")]
    [InlineData("ExtensionPoints/Tools/Policy.cs", "namespace BotNexus.Agent.Core.ExtensionPoints.Tools; public record Policy(")]
    public void LayoutInspector_InvalidLayoutOrSyntax_ReportsViolations(string path, string source)
    {
        FindViolations(path, source).ShouldNotBeEmpty();
    }

    [Fact]
    public void LayoutInspector_ExistingToolUpdateCallback_RemainsRuntimeData()
    {
        FindViolations("Types/AgentToolUpdateCallback.cs",
            "namespace BotNexus.Agent.Core.Types; public delegate void AgentToolUpdateCallback();")
            .ShouldBeEmpty();
        FindViolations("Configuration/AgentToolUpdateCallback.cs",
            "namespace BotNexus.Agent.Core.Configuration; public delegate void AgentToolUpdateCallback();")
            .ShouldNotBeEmpty();
    }

    private static IReadOnlyList<string> FindViolations(string path, string source)
    {
        var initialTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var symbols = initialTree.GetRoot().DescendantTrivia(descendIntoTrivia: true)
            .Select(trivia => trivia.GetStructure()).OfType<DirectiveTriviaSyntax>()
            .Where(directive => directive is IfDirectiveTriviaSyntax or ElifDirectiveTriviaSyntax)
            .SelectMany(directive => directive.DescendantTokens())
            .Where(token => token.IsKind(SyntaxKind.IdentifierToken))
            .Select(token => token.ValueText).Distinct(StringComparer.Ordinal).ToArray();
        // Bound inspection cost rather than silently ignoring uninspected conditional branches.
        if (symbols.Length > 8)
            return [$"{path}: too many conditional symbols; extend the inspector before adding this layout"];
        return Enumerable.Range(0, 1 << symbols.Length)
            .SelectMany(mask => FindActiveViolations(path, source,
                symbols.Where((_, index) => (mask & (1 << index)) != 0)))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> FindActiveViolations(string path, string source, IEnumerable<string> symbols)
    {
        var violations = new List<string>();
        var parts = path.Split('/');
        if (parts.Any(part => part.Equals("Hooks", StringComparison.OrdinalIgnoreCase))
            || parts[^1].Equals("Delegates.cs", StringComparison.OrdinalIgnoreCase))
            violations.Add($"{path}: retired timing bucket or delegate collection");

        var tree = CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: symbols));
        foreach (var diagnostic in tree.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error))
            violations.Add($"{path}: {diagnostic}");

        var root = tree.GetRoot();
        foreach (var ns in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            if (NamespaceOf(ns).Split('.').Contains("Hooks", StringComparer.OrdinalIgnoreCase))
                violations.Add($"{path}: retired Hooks namespace");
            if (parts[0] == "ExtensionPoints"
                && NamespaceOf(ns) != "BotNexus.Agent.Core." + string.Join('.', parts[..^1])
                && !ns.Members.OfType<BaseNamespaceDeclarationSyntax>().Any())
                violations.Add($"{path}: namespace must match the responsibility-family directory");
        }

        var declarations = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(node => node.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .Where(node => ModifiersOf(node).Any(SyntaxKind.PublicKeyword))
            .ToArray();
        var inExtensionTree = parts[0] == "ExtensionPoints";
        if (inExtensionTree && parts.Length < 3)
            violations.Add($"{path}: missing responsibility family");
        if (inExtensionTree && declarations.Length > 1)
            violations.Add($"{path}: expected at most one public namespace-level declaration, found {declarations.Length}");

        foreach (var declaration in declarations)
        {
            var name = declaration switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                DelegateDeclarationSyntax callback => callback.Identifier.ValueText,
                _ => throw new InvalidOperationException("Unsupported declaration")
            };
            var ns = NamespaceOf(declaration);
            // The streaming tool-update callback predates the responsibility-family layout and
            // remains runtime data. This exact exception must not exempt other raw callbacks.
            var existingCallback = path == "Types/AgentToolUpdateCallback.cs"
                && name == "AgentToolUpdateCallback" && ns == "BotNexus.Agent.Core.Types";
            var extensionContract = inExtensionTree
                || ns == "BotNexus.Agent.Core.ExtensionPoints"
                || ns.StartsWith("BotNexus.Agent.Core.ExtensionPoints.", StringComparison.Ordinal)
                || declaration is DelegateDeclarationSyntax && !existingCallback;
            if (!extensionContract)
                continue;
            if (!inExtensionTree)
                violations.Add($"{path}: {name} belongs under ExtensionPoints/<responsibility>");
            var expectedNamespace = "BotNexus.Agent.Core." + string.Join('.', parts[..^1]);
            if (ns != expectedNamespace)
                violations.Add($"{path}: {name} namespace {ns} must be {expectedNamespace}");
            if (parts[^1] != name + ".cs")
                violations.Add($"{path}: public declaration {name} requires a matching filename");
        }
        return violations;
    }

    private static SyntaxTokenList ModifiersOf(MemberDeclarationSyntax declaration) => declaration switch
    {
        BaseTypeDeclarationSyntax type => type.Modifiers,
        DelegateDeclarationSyntax callback => callback.Modifiers,
        _ => default
    };

    private static string NamespaceOf(SyntaxNode node) => string.Join('.', node.AncestorsAndSelf()
        .OfType<BaseNamespaceDeclarationSyntax>().Reverse()
        .SelectMany(ns => ns.Name.DescendantTokens().Where(token => token.IsKind(SyntaxKind.IdentifierToken)))
        .Select(token => token.ValueText));
}