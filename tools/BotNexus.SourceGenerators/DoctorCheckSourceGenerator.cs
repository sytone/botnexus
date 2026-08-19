namespace BotNexus.SourceGenerators;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>
/// Roslyn incremental generator that turns <c>[DoctorCheck]</c>-annotated check classes into the
/// doctor registries and a machine-readable id inventory (#3319).
/// </summary>
/// <remarks>
/// <para>
/// The defect class this removes is the one #2041 opened the seam for but did not close: the
/// registry was a hand-written array, so a check could be written, tested and left unregistered.
/// That failure is invisible - a rule structurally incapable of firing reads as a clean pass, the
/// #2700 shape. The registries are now projections of the declarations, so there is no second list
/// to forget.
/// </para>
/// <para>
/// It also feeds the docs fence. <c>DoctorCheckIds.All</c> is the inventory a test diffs against
/// <c>docs/cli-reference.md</c>; the fence fails naming any id that is registered but undocumented.
/// Generating the docs PROSE is deliberately out of scope - a generated sentence would satisfy the
/// fence without telling an operator anything, so the fence fails and a human writes the sentence.
/// </para>
/// <para>
/// <b>No new diagnostics.</b> Unlike #2769 and #3320 this generator reports none: its only
/// malformed-input cases (a missing or duplicated id) are asserted by fences in the test suite
/// instead, which keeps the analyzer release-tracking surface unchanged.
/// </para>
/// </remarks>
[Generator]
public sealed class DoctorCheckSourceGenerator : IIncrementalGenerator
{
    /// <summary>Fully-qualified name of the marker attribute that opts a check in.</summary>
    public const string DoctorCheckAttributeName = "BotNexus.Cli.Commands.Doctor.Generated.DoctorCheckAttribute";

    /// <summary>Namespace the registries are emitted into when MSBuild declares no override.</summary>
    public const string DefaultNamespace = "BotNexus.Cli.Commands.Doctor";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static production =>
            production.AddSource("DoctorCheckAttribute.g.cs", DoctorCheckCodeGenerator.AttributeSource));

        var declarations = context.SyntaxProvider.ForAttributeWithMetadataName(
                DoctorCheckAttributeName,
                predicate: static (_, _) => true,
                transform: static (syntaxContext, _) => BuildModel(syntaxContext.TargetSymbol as INamedTypeSymbol, syntaxContext.Attributes))
            .Where(static model => model is not null)
            .Collect();

        var namespaceProvider = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => ExtractNamespace(provider));

        context.RegisterSourceOutput(
            declarations.Combine(namespaceProvider),
            static (production, pair) =>
            {
                var (checks, namespaceName) = pair;

                // Every project that references the generator as an analyzer receives the attribute
                // source, but only the CLI declares checks. Emitting an empty registry elsewhere
                // would reference IDoctorCheck in a project that has never heard of it, so an empty
                // declaration set produces no registry at all.
                if (checks.IsDefaultOrEmpty)
                {
                    return;
                }

                production.AddSource(
                    "GeneratedDoctorChecks.g.cs",
                    DoctorCheckCodeGenerator.Generate(checks.ToList(), namespaceName));
            });
    }

    /// <summary>
    /// Reads the MSBuild-visible namespace override. Public so option extraction is pinned directly
    /// by tests rather than only inferred from a full compilation.
    /// </summary>
    public static string ExtractNamespace(AnalyzerConfigOptionsProvider provider)
        => provider.GlobalOptions.TryGetValue("build_property.DoctorCheckSourceGenerator_Namespace", out var ns)
            && !string.IsNullOrWhiteSpace(ns)
                ? ns
                : DefaultNamespace;

    /// <summary>
    /// Projects one annotated symbol into a model. Public so the attribute-reading rules can be
    /// exercised without standing up a compilation for each case.
    /// </summary>
    public static DoctorCheckModel BuildModel(INamedTypeSymbol symbol, IEnumerable<AttributeData> attributes)
    {
        if (symbol is null || attributes is null)
        {
            return null;
        }

        var attribute = attributes.FirstOrDefault(
            candidate => candidate.AttributeClass?.ToDisplayString() == DoctorCheckAttributeName);

        if (attribute is null)
        {
            return null;
        }

        var model = new DoctorCheckModel
        {
            Id = string.Empty,
            Suite = DoctorSuiteNames.Aggregate,
            Order = 0,
            TypeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", string.Empty)
        };

        foreach (var named in attribute.NamedArguments)
        {
            switch (named.Key)
            {
                case "Id":
                    model.Id = named.Value.Value as string ?? string.Empty;
                    break;
                case "Suite":
                    model.Suite = SuiteName(named.Value.Value);
                    break;
                case "Order":
                    model.Order = named.Value.Value is int order ? order : 0;
                    break;
            }
        }

        return model;
    }

    /// <summary>
    /// Maps the generated <c>DoctorSuite</c> enum value onto its name. The suite is DECLARED rather
    /// than inferred from the implemented interface on purpose: <c>DoctorConfigCommand</c> documents
    /// that advisories are a separate list because they have no <c>Apply</c> and must never be
    /// applied by <c>--yes</c>, and a heuristic that got that wrong would wire one in silently.
    /// </summary>
    public static string SuiteName(object value)
        => value switch
        {
            1 => DoctorSuiteNames.Config,
            2 => DoctorSuiteNames.Advisory,
            _ => DoctorSuiteNames.Aggregate
        };
}
