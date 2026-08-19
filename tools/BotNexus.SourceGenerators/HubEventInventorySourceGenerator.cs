namespace BotNexus.SourceGenerators;

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Roslyn incremental generator that derives the SignalR hub event inventory from the interface
/// that already declares the server→client contract (#3318).
/// </summary>
/// <remarks>
/// <para>
/// <c>IGatewayHubClient</c> is the single declaration of the contract, but it was restated by hand
/// in the integration harnesses, which carried 13 of its 24 events. The eleven missing events were
/// unobservable to those suites: the harness never subscribed, so a regression surfaced as an empty
/// event list rather than a failure. This generator emits the inventory from the interface's own
/// members, so the harness inventory cannot drift from the declaration.
/// </para>
/// <para>
/// <b>An empty inventory is a build error (<c>BNHE001</c>)</b>, following the #2769 precedent: a
/// silently empty <c>All</c> array would make every harness stop subscribing to everything, which
/// is precisely the "nothing was received" failure this exists to remove. Naming the cause at build
/// time beats a green run that observed nothing.
/// </para>
/// </remarks>
[Generator]
public sealed class HubEventInventorySourceGenerator : IIncrementalGenerator
{
    /// <summary>Fully-qualified name of the marker attribute that opts an interface in.</summary>
    public const string HubEventInventoryAttributeName = "BotNexus.SourceGenerators.Generated.HubEventInventoryAttribute";

    /// <summary>Diagnostic ID reported when an annotated interface declares no events.</summary>
    public const string EmptyInventoryId = "BNHE001";

    private static readonly DiagnosticDescriptor EmptyInventory = new(
        EmptyInventoryId,
        "Hub event inventory is empty",
        "Interface '{0}' is marked [HubEventInventory] but declares no methods, so the generated inventory would be empty",
        "HubEvents",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An empty inventory makes every consumer subscribe to nothing, which presents "
            + "as a quiet passing run rather than a failure - the exact defect this generator removes.");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static production =>
            production.AddSource("HubEventInventoryAttribute.g.cs", HubEventInventoryCodeGenerator.AttributeSource));

        var declarations = context.SyntaxProvider.ForAttributeWithMetadataName(
            HubEventInventoryAttributeName,
            predicate: static (_, _) => true,
            transform: static (syntaxContext, _) => BuildModel(syntaxContext.TargetSymbol as INamedTypeSymbol, syntaxContext.Attributes));

        context.RegisterSourceOutput(declarations, static (production, result) =>
        {
            if (result is null)
            {
                return;
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                production.ReportDiagnostic(diagnostic);
            }

            if (result.Diagnostics.Count > 0 || result.Model is null)
            {
                return;
            }

            production.AddSource(
                $"{result.Model.ClassName}.HubEvents.g.cs",
                HubEventInventoryCodeGenerator.Generate(result.Model));
        });
    }

    /// <summary>
    /// Reads the event names off an interface symbol. Public so the projection rule is pinned by
    /// unit tests directly rather than only inferred from a full compilation.
    /// </summary>
    /// <remarks>
    /// Only ordinary methods are projected. Property and event members are not part of the SignalR
    /// server→client method contract, and including their accessor names would emit subscriptions
    /// for handlers the server never invokes.
    /// </remarks>
    public static IReadOnlyList<string> ExtractEventNames(INamedTypeSymbol symbol) =>
        symbol is null
            ? new List<string>()
            : symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(method => method.MethodKind == MethodKind.Ordinary)
                .Select(method => method.Name)
                .ToList();

    private static GenerationResult BuildModel(INamedTypeSymbol symbol, IReadOnlyList<AttributeData> attributes)
    {
        if (symbol is null)
        {
            return null;
        }

        var className = "HubEvents";
        foreach (var attribute in attributes)
        {
            foreach (var named in attribute.NamedArguments)
            {
                if (named.Key == "ClassName" && named.Value.Value is string configured && configured.Length > 0)
                {
                    className = configured;
                }
            }
        }

        var names = ExtractEventNames(symbol);
        if (names.Count == 0)
        {
            var location = symbol.Locations.FirstOrDefault() ?? Location.None;
            return new GenerationResult(null, new List<Diagnostic> { Diagnostic.Create(EmptyInventory, location, symbol.Name) });
        }

        return new GenerationResult(
            new HubEventInventoryModel
            {
                Namespace = symbol.ContainingNamespace.ToDisplayString(),
                ClassName = className,
                SourceInterfaceName = symbol.Name,
                EventNames = names
            },
            new List<Diagnostic>());
    }

    /// <summary>Model plus diagnostics from one declaration site.</summary>
    private sealed class GenerationResult(HubEventInventoryModel model, IReadOnlyList<Diagnostic> diagnostics)
    {
        public HubEventInventoryModel Model { get; } = model;

        public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
    }
}
