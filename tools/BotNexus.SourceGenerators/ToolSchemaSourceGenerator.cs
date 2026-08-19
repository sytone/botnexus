namespace BotNexus.SourceGenerators;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Roslyn incremental generator that turns a <c>[ToolSchema]</c>-annotated partial class carrying
/// <c>[ToolParameter]</c> declarations into BOTH the tool's JSON schema string and its prepare-stage
/// parameter table (#3320).
/// </summary>
/// <remarks>
/// <para>
/// The defect class this removes is #2641: a parameter declared in the JSON schema but omitted from
/// the <c>PrepareArgumentsAsync</c> copy list. That omission fails INVISIBLY - the caller's value is
/// dropped and the default answers instead, which is a plausible number for the wrong question. Both
/// representations are now projections of one declaration, so the two cannot disagree.
/// </para>
/// <para>
/// It also removes the #2415 class (schema says X, reader reads Y) for the declared keys, because the
/// reader iterates the generated table rather than a hand-written sequence of <c>TryGetValue</c> calls.
/// It does NOT address #2690, where callers send malformed values for a correctly-described parameter -
/// no amount of declaration agreement constrains what a caller actually sends, which is why the
/// coercion helpers stay.
/// </para>
/// <para>
/// <b>Malformed input is a build error, not silence</b>, following the #2769 precedent: an unknown
/// JSON type (<c>BNTS001</c>), a duplicate parameter name (<c>BNTS002</c>) or an alias pointing at a
/// key that is not declared before it (<c>BNTS003</c>) stops the build with a diagnostic that names
/// the cause, rather than emitting nothing and producing a cascade of errors at innocent call sites.
/// </para>
/// </remarks>
[Generator]
public sealed class ToolSchemaSourceGenerator : IIncrementalGenerator
{
    /// <summary>Fully-qualified name of the marker attribute that opts a container in.</summary>
    public const string ToolSchemaAttributeName = "BotNexus.Agent.Core.Tools.Generated.ToolSchemaAttribute";

    /// <summary>Fully-qualified name of the per-parameter declaration attribute.</summary>
    public const string ToolParameterAttributeName = "BotNexus.Agent.Core.Tools.Generated.ToolParameterAttribute";

    /// <summary>Diagnostic ID reported for a JSON Schema type keyword that is not recognised.</summary>
    public const string UnknownJsonTypeId = "BNTS001";

    /// <summary>Diagnostic ID reported when two parameters declare the same name.</summary>
    public const string DuplicateParameterId = "BNTS002";

    /// <summary>Diagnostic ID reported when an alias targets a key that is not declared before it.</summary>
    public const string UnresolvedAliasId = "BNTS003";

    /// <summary>The JSON Schema type keywords a parameter may declare.</summary>
    private static readonly ImmutableHashSet<string> AllowedJsonTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal, "string", "integer", "number", "boolean", "array", "object");

    private static readonly DiagnosticDescriptor UnknownJsonType = new(
        UnknownJsonTypeId,
        "Unknown tool parameter JSON type",
        "Tool parameter '{0}' declares JSON type '{1}', which is not a JSON Schema type keyword. Use one of: array, boolean, integer, number, object, string.",
        "ToolSchema",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A misspelled type would be emitted verbatim into the schema sent to the model, "
            + "which the model would silently fail to satisfy. Failing the build names the cause instead.");

    private static readonly DiagnosticDescriptor DuplicateParameter = new(
        DuplicateParameterId,
        "Duplicate tool parameter declaration",
        "Tool parameter '{0}' is declared more than once",
        "ToolSchema",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Two declarations of one key make the emitted schema and the prepare table "
            + "depend on declaration order, which is exactly the ambiguity this generator exists to remove.");

    private static readonly DiagnosticDescriptor UnresolvedAlias = new(
        UnresolvedAliasId,
        "Tool parameter alias targets an undeclared key",
        "Tool parameter '{0}' aliases '{1}', which is not declared before it",
        "ToolSchema",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An alias whose target is missing would copy into a key nothing ever reads - "
            + "the #2641 failure mode, reintroduced through the alias path.");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static production =>
            production.AddSource("ToolSchemaAttributes.g.cs", ToolSchemaCodeGenerator.AttributeSource));

        var declarations = context.SyntaxProvider.ForAttributeWithMetadataName(
            ToolSchemaAttributeName,
            predicate: static (_, _) => true,
            transform: static (syntaxContext, _) => BuildModel(syntaxContext.TargetSymbol as INamedTypeSymbol));

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
                $"{result.Model.ContainerName}.ToolSchema.g.cs",
                ToolSchemaCodeGenerator.Generate(result.Model));
        });
    }

    /// <summary>
    /// Validates a parameter set and reports every rule violation. Exposed for direct unit testing so
    /// the sad paths can be asserted without standing up a full compilation for each one.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Validate(IReadOnlyList<ToolParameterModel> parameters, Location location)
    {
        var diagnostics = new List<Diagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in parameters)
        {
            if (!AllowedJsonTypes.Contains(parameter.JsonType ?? string.Empty))
            {
                diagnostics.Add(Diagnostic.Create(UnknownJsonType, location, parameter.Name, parameter.JsonType));
            }

            if (!seen.Add(parameter.Name ?? string.Empty))
            {
                diagnostics.Add(Diagnostic.Create(DuplicateParameter, location, parameter.Name));
            }

            if (!string.IsNullOrEmpty(parameter.AliasOf) && !seen.Contains(parameter.AliasOf))
            {
                diagnostics.Add(Diagnostic.Create(UnresolvedAlias, location, parameter.Name, parameter.AliasOf));
            }
        }

        return diagnostics;
    }

    private static GenerationResult BuildModel(INamedTypeSymbol symbol)
    {
        if (symbol is null)
        {
            return null;
        }

        var parameters = new List<ToolParameterModel>();
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != ToolParameterAttributeName)
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length < 2)
            {
                continue;
            }

            var parameter = new ToolParameterModel
            {
                Name = attribute.ConstructorArguments[0].Value as string ?? string.Empty,
                JsonType = attribute.ConstructorArguments[1].Value as string ?? string.Empty,
                Description = string.Empty,
                AliasOf = string.Empty
            };

            foreach (var named in attribute.NamedArguments)
            {
                switch (named.Key)
                {
                    case "Description":
                        parameter.Description = named.Value.Value as string ?? string.Empty;
                        break;
                    case "Required":
                        parameter.Required = named.Value.Value is bool required && required;
                        break;
                    case "AliasOf":
                        parameter.AliasOf = named.Value.Value as string ?? string.Empty;
                        break;
                    case "HiddenFromSchema":
                        parameter.HiddenFromSchema = named.Value.Value is bool hidden && hidden;
                        break;
                }
            }

            parameters.Add(parameter);
        }

        // GetAttributes() returns repeated attributes in source declaration order, and that order is
        // load-bearing twice over: alias targets must already be seen (BNTS003), and schema property
        // order is what AC2's comparison against the hand-written literal is measured on.
        var ordered = parameters;

        var location = symbol.Locations.FirstOrDefault() ?? Location.None;
        var diagnostics = Validate(ordered, location);

        return new GenerationResult(
            diagnostics.Count > 0
                ? null
                : new ToolSchemaModel
                {
                    Namespace = symbol.ContainingNamespace.ToDisplayString(),
                    ContainerName = symbol.Name,
                    Parameters = ordered
                },
            diagnostics);
    }

    /// <summary>Model plus diagnostics from one declaration site.</summary>
    private sealed class GenerationResult(ToolSchemaModel model, IReadOnlyList<Diagnostic> diagnostics)
    {
        public ToolSchemaModel Model { get; } = model;

        public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
    }
}
