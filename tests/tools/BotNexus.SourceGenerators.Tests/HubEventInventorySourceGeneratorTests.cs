namespace BotNexus.SourceGenerators.Tests;

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Covers the hub event inventory generator (#3318): what it projects off an interface, what it
/// emits, and the empty-declaration case that must be a build ERROR rather than silence.
/// </summary>
public class HubEventInventorySourceGeneratorTests
{
    [Fact]
    public void ExtractedNames_FollowInterfaceDeclarationOrder()
    {
        var symbol = CompileInterface("""
            namespace Sample
            {
                public interface IHub
                {
                    System.Threading.Tasks.Task Connected(string a);
                    System.Threading.Tasks.Task RunStarted(string a);
                    System.Threading.Tasks.Task RunEnded(string a);
                }
            }
            """);

        HubEventInventorySourceGenerator.ExtractEventNames(symbol)
            .ShouldBe(new List<string> { "Connected", "RunStarted", "RunEnded" });
    }

    [Fact]
    public void AddingAMemberToTheInterface_GrowsTheInventoryWithNoOtherEdit()
    {
        // The whole point: the inventory is a projection, so a new member cannot fail to reach it.
        // With a hand-written list this test is the one that would go red.
        var before = HubEventInventorySourceGenerator.ExtractEventNames(CompileInterface("""
            namespace Sample
            {
                public interface IHub { System.Threading.Tasks.Task Connected(string a); }
            }
            """));

        var after = HubEventInventorySourceGenerator.ExtractEventNames(CompileInterface("""
            namespace Sample
            {
                public interface IHub
                {
                    System.Threading.Tasks.Task Connected(string a);
                    System.Threading.Tasks.Task BrandNewEvent(string a);
                }
            }
            """));

        after.Count.ShouldBe(before.Count + 1);
        after.ShouldContain("BrandNewEvent");
    }

    [Fact]
    public void PropertyAccessors_AreNotProjectedAsEvents()
    {
        // get_/set_ accessors are methods on the symbol but not hub methods; emitting them would
        // subscribe every consumer to handlers the server never invokes.
        var symbol = CompileInterface("""
            namespace Sample
            {
                public interface IHub
                {
                    string Name { get; }
                    System.Threading.Tasks.Task Connected(string a);
                }
            }
            """);

        HubEventInventorySourceGenerator.ExtractEventNames(symbol).ShouldBe(new List<string> { "Connected" });
    }

    [Fact]
    public void GeneratedSource_EmitsEveryNameAsAStringLiteral()
    {
        var generated = HubEventInventoryCodeGenerator.Generate(Model("Connected", "RunEnded"));

        generated.ShouldContain("public static class HubEvents");
        generated.ShouldContain("\"Connected\",");
        generated.ShouldContain("\"RunEnded\",");
    }

    [Fact]
    public void GeneratedSource_IsIdenticalAcrossRuns()
    {
        // Roslyn caches generator output; content varying between runs would invalidate that cache
        // and make two builds of one commit produce different source.
        var model = Model("Connected", "RunEnded");

        HubEventInventoryCodeGenerator.Generate(model).ShouldBe(HubEventInventoryCodeGenerator.Generate(model));
    }

    [Fact]
    public void EmptyInterface_IsABuildError()
    {
        // A silently empty inventory would make every harness subscribe to nothing - the exact
        // "nothing was received" failure this generator exists to remove. It must not compile.
        var driver = CSharpGeneratorDriver
            .Create(new HubEventInventorySourceGenerator())
            .RunGeneratorsAndUpdateCompilation(
                Compile("""
                    namespace BotNexus.SourceGenerators.Generated
                    {
                        [System.AttributeUsage(System.AttributeTargets.Interface)]
                        internal sealed class HubEventInventoryAttribute : System.Attribute
                        {
                            public string ClassName { get; set; }
                        }
                    }

                    namespace Sample
                    {
                        [BotNexus.SourceGenerators.Generated.HubEventInventory]
                        public interface IEmptyHub { }
                    }
                    """),
                out _,
                out var diagnostics);

        _ = driver;
        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe(HubEventInventorySourceGenerator.EmptyInventoryId);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("IEmptyHub");
    }

    [Fact]
    public void AnnotatedInterfaceWithMembers_ProducesTheInventoryAndNoDiagnostics()
    {
        // Non-vacuity for the error test above: the happy path must actually emit, otherwise the
        // error assertion would pass even if the generator rejected everything.
        CSharpGeneratorDriver
            .Create(new HubEventInventorySourceGenerator())
            .RunGeneratorsAndUpdateCompilation(
                Compile("""
                    namespace BotNexus.SourceGenerators.Generated
                    {
                        [System.AttributeUsage(System.AttributeTargets.Interface)]
                        internal sealed class HubEventInventoryAttribute : System.Attribute
                        {
                            public string ClassName { get; set; }
                        }
                    }

                    namespace Sample
                    {
                        [BotNexus.SourceGenerators.Generated.HubEventInventory]
                        public interface IHub
                        {
                            System.Threading.Tasks.Task Connected(string a);
                        }
                    }
                    """),
                out var updated,
                out var diagnostics);

        diagnostics.ShouldBeEmpty();
        updated.SyntaxTrees.Any(tree => tree.ToString().Contains("public static class HubEvents")).ShouldBeTrue();
    }

    private static HubEventInventoryModel Model(params string[] names) => new()
    {
        Namespace = "Sample",
        ClassName = "HubEvents",
        SourceInterfaceName = "IHub",
        EventNames = names.ToList()
    };

    private static INamedTypeSymbol CompileInterface(string source)
    {
        var compilation = Compile(source);
        return compilation.GetSymbolsWithName(name => name.StartsWith("I"), SymbolFilter.Type)
            .OfType<INamedTypeSymbol>()
            .First(symbol => symbol.TypeKind == TypeKind.Interface);
    }

    private static CSharpCompilation Compile(string source) => CSharpCompilation.Create(
        "HubEventInventoryTestAssembly",
        [CSharpSyntaxTree.ParseText(source)],
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location)
        ],
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
